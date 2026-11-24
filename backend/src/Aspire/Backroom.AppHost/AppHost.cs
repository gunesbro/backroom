using Backroom.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var run = DevelopmentRun.FromConfiguration(builder.Configuration);
run.WarnIfIsolated(builder);
var cells = DevelopmentCells.Selected(builder.Configuration);
var services = builder.AddDevelopmentServices(run);
var keycloak = services.Keycloak;
var databases = new DevelopmentDatabases(builder, services.Postgres);

// Services validate tokens against the realm; its URL is also the token issuer.
var authority = ReferenceExpression.Create(
    $"{keycloak.GetEndpoint("http").Property(EndpointProperty.Url)}/realms/{DevelopmentServices.KeycloakRealm}");

// Each cell's secrets are Aspire parameters named after the cell id (docs/contracts/phase-1.md, "Cell registration" and
// "Invitation security"). Keys are read as base64: 44 letters and digits are valid base64 of 33 bytes.
IResourceBuilder<ParameterResource> GeneratedSecret(string name, int minLength) =>
    builder.AddParameter(name, new GenerateParameterDefault { MinLength = minLength, Special = false }, secret: true, persist: true);

var cellSecrets = cells.ToDictionary(
    cell => cell.Id,
    cell => new
    {
        // One-time registration key: the control plane accepts it for that cell, the cell's worker registers with it on first start.
        RegistrationKey = GeneratedSecret($"cell-{cell.Id}-registration-key", 43),
        InvitationKey = GeneratedSecret($"cell-{cell.Id}-invitation-key", 44),
        // Key for the recipient hashes in the cell's email delivery log (docs/contracts/phase-1.md, "Data").
        EmailRecipientHashKey = GeneratedSecret($"cell-{cell.Id}-email-recipient-hash-key", 44),
    });

// The control plane's key for invited emails (docs/contracts/phase-1.md, "Data"); read as base64, like the cell's key.
var emailHmacKey = GeneratedSecret("control-plane-email-hmac-key", 44);

// Control plane. Its migrate run starts first, because it also creates the shared development roles.
var controlPlaneMigrate = builder.AddProject<Projects.Backroom_ControlPlane_Host>(
        "control-plane-migrate", options => options.ExcludeLaunchProfile = true)
    .WithArgs("migrate");

var controlPlane = builder.AddProject<Projects.Backroom_ControlPlane_Host>("control-plane")
    .WithEnvironment("Authentication__Authority", authority)
    .WithEnvironment("CellManagement__Keycloak__AdminClientSecret", services.ControlPlaneAdminSecret)
    .WithEnvironment("Features__OrganizationCreation", "true")
    .WithWebOrigins()
    .WithEnvironment("Features__Invitations", "true")
    .WithEnvironment("Directory__Invitations__EmailHmacKey", emailHmacKey)
    .WaitFor(keycloak)
    .WithHttpHealthCheck("/health");

// The shared development cells, seeded as pending cells; each one's public URL is set with its app below.
for (var i = 0; i < cells.Count; i++)
{
    controlPlane
        .WithEnvironment($"CellManagement__DevelopmentCells__{i}__Id", cells[i].Id)
        .WithEnvironment($"CellManagement__DevelopmentCells__{i}__Name", cells[i].Name)
        .WithEnvironment($"CellManagement__DevelopmentCells__{i}__RegistrationKey", cellSecrets[cells[i].Id].RegistrationKey);
}

// Keycloak ends a session by calling the control plane (OIDC Back-Channel Logout, docs/contracts/phase-1.md, flow 5). It runs in a
// container and the control plane on the host, so it reaches it through the container host name, which the control plane must
// accept (AllowedHosts): Aspire 13 resolves the reference to aspire.dev.internal (its container tunnel); host.docker.internal is
// kept for runs without the tunnel. Plain HTTP: the container does not trust the development certificate; deployments use https.
keycloak.WithEnvironment(
    "BACKROOM_BACKCHANNEL_LOGOUT_URL",
    ReferenceExpression.Create($"{controlPlane.GetEndpoint("http").Property(EndpointProperty.Url)}/internal/keycloak/backchannel-logout"));
controlPlane.WithEnvironment("AllowedHosts", "localhost;127.0.0.1;[::1];aspire.dev.internal;host.docker.internal");

databases.Add("control_plane", controlPlaneMigrate, controlPlane);

// Cells share the development PostgreSQL server, Valkey and RabbitMQ (docs/contracts/phase-1.md, "Development topology"):
// a database each, a virtual host each, and a Valkey key and channel prefix from the cell id. The roles are server-wide, so
// all databases use the same role passwords. In production each cell has its own instances.
var virtualHosts = builder.AddRabbitMqVirtualHosts(services.RabbitMq, cells);

// Every migrate run creates or updates the server-wide roles, and PostgreSQL refuses concurrent updates of the same role
// ("tuple concurrently updated"), so the runs go one after another: each cell's waits for the previous one.
var previousMigrate = controlPlaneMigrate;

// Each cell's key and registration marker live in a directory of this AppHost (the hash its volumes carry), so a
// worktree or second checkout never takes another environment's "registered" marker for its own. An isolated run keeps
// them under the root its caller gives (and removes), because it starts with a fresh control plane every time.
var appHostId = DevelopmentKeyDirectories.AppHostIdFromVolumeName(VolumeNameGenerator.Generate(services.Postgres, "data"), "postgres-data");
var keyDirectoryRoot = run.KeyDirectoryRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

for (var i = 0; i < cells.Count; i++)
{
    var cell = cells[i];
    var secrets = cellSecrets[cell.Id];

    // The same binary runs as the "migrate", "app" and "worker" role.
    // The role is set explicitly; launch profiles only supply ports.
    var cellMigrate = builder.AddProject<Projects.Backroom_Cell_Host>(
            $"cell-{cell.Id}-migrate", options => options.ExcludeLaunchProfile = true)
        .WithEnvironment("Cell__Role", "migrate")
        .WaitForCompletion(previousMigrate);
    previousMigrate = cellMigrate;

    var cellApp = builder.AddProject<Projects.Backroom_Cell_Host>($"cell-{cell.Id}-app", launchProfileName: "app" + cell.LaunchProfileSuffix)
        .WithEnvironment("Cell__Id", cell.Id)
        .WithEnvironment("Cell__Role", "app")
        // Invitations (flow 3) are behind a flag; on in development, as on the control plane.
        .WithEnvironment("Features__Invitations", "true")
        .WithWebOrigins()
        .WithEnvironment("Authentication__Authority", authority)
        // Live events: the SignalR backplane and the member-removal channel (docs/contracts/phase-1.md, "Live events"),
        // separated per cell by the prefix the cell derives from its id.
        .WithEnvironment("Valkey__ConnectionString", services.Valkey.Resource.ConnectionStringExpression)
        .WaitFor(keycloak)
        .WaitFor(services.Valkey)
        .WithHttpHealthCheck("/health");

    // Files (Phase 2, behind Features:Files): the app role signs uploads and downloads for the cell's own bucket. The
    // browser reaches SeaweedFS on localhost, so the signed URLs name the same endpoint. An isolated run has no object
    // storage, so the flag stays off there.
    if (services.ObjectStorage is { } objectStorage)
    {
        cellApp
            .WithEnvironment("Features__Files", "true")
            .WithEnvironment("Files__Storage__ServiceUrl", objectStorage.S3Endpoint)
            .WithEnvironment("Files__Storage__Bucket", DevelopmentObjectStorage.BucketFor(cell))
            .WithEnvironment("Files__Storage__AccessKeyId", objectStorage.AccessKeyId)
            .WithEnvironment("Files__Storage__SecretAccessKey", objectStorage.SecretAccessKey)
            .WithEnvironment("Files__Storage__CreateBucket", "true")
            .WaitFor(objectStorage.Container);
    }

    // The cell's public URL is its app endpoint (the launch profile's port, or a random one in an isolated run), which the
    // web app learns from /api/v1/cells/shared and the organization data.
    controlPlane.WithEnvironment($"CellManagement__DevelopmentCells__{i}__PublicUrl", cellApp.GetEndpoint("http"));

    // The worker registers the cell with the control plane on its first start, then keeps its link
    // (gRPC over HTTP/2, so it uses the https endpoint). It also relays the modules' outboxes to RabbitMQ and
    // runs their event consumers; the app role only writes outboxes, so only the worker gets RabbitMQ.
    var cellWorker = builder.AddProject<Projects.Backroom_Cell_Host>($"cell-{cell.Id}-worker", launchProfileName: "worker" + cell.LaunchProfileSuffix)
        .WithEnvironment("Cell__Id", cell.Id)
        .WithEnvironment("Cell__Role", "worker")
        .WithEnvironment("Authentication__Authority", authority)
        .WithEnvironment("ControlPlaneConnector__ControlPlaneUrl", controlPlane.GetEndpoint("https"))
        .WithEnvironment("ControlPlaneConnector__RegistrationKey", secrets.RegistrationKey)
        .WithEnvironment("ControlPlaneConnector__KeyDirectory", DevelopmentKeyDirectories.For(keyDirectoryRoot, appHostId, cell.Id))
        .WithEnvironment("RabbitMq__ConnectionString", ReferenceExpression.Create($"{services.RabbitMq.Resource.ConnectionStringExpression}/{cell.VirtualHost}"))
        .WithEnvironment("RabbitMq__ClientName", $"cell-{cell.Id}-worker")
        // Only the worker sends email, and only to Mailpit (docs/adr/0008); the app role gets no SMTP endpoint.
        .WithEnvironment("Email__Mailpit__Host", services.Mailpit.Resource.Host)
        .WithEnvironment("Email__Mailpit__Port", services.Mailpit.Resource.Port)
        .WithEnvironment("Email__FromAddress", "no-reply@backroom.test")
        .WithEnvironment("Email__FromName", "Backroom")
        .WithEnvironment("Email__RecipientHashKey", secrets.EmailRecipientHashKey)
        // Invitation emails link to the web app's dev server (frontend/apps/web, fixed port); http only on loopback in Development.
        .WithEnvironment("Organization__InvitationLinks__WebOrigin", "http://localhost:5173")
        .WaitFor(keycloak)
        .WithEnvironment("Valkey__ConnectionString", services.Valkey.Resource.ConnectionStringExpression)
        .WaitFor(services.RabbitMq)
        .WaitForCompletion(virtualHosts)
        .WaitFor(services.Valkey)
        .WaitFor(services.Mailpit)
        .WaitFor(controlPlane)
        .WithHttpHealthCheck("/health");

    // Files: the worker scans completed uploads (step 23) from the same bucket through clamd on localhost.
    if (services.ObjectStorage is { } workerStorage && services.FileScanner is { } scanner)
    {
        cellWorker
            .WithEnvironment("Features__Files", "true")
            .WithEnvironment("Files__Storage__ServiceUrl", workerStorage.S3Endpoint)
            .WithEnvironment("Files__Storage__Bucket", DevelopmentObjectStorage.BucketFor(cell))
            .WithEnvironment("Files__Storage__AccessKeyId", workerStorage.AccessKeyId)
            .WithEnvironment("Files__Storage__SecretAccessKey", workerStorage.SecretAccessKey)
            .WithEnvironment("Files__Scanner__Host", "localhost")
            .WithEnvironment("Files__Scanner__Port", DevelopmentServices.ClamAvPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WaitFor(workerStorage.Container)
            .WaitFor(scanner);
    }

    foreach (var host in (IResourceBuilder<ProjectResource>[])[cellApp, cellWorker])
    {
        host.WithEnvironment("Organization__InvitationKeys__CurrentVersion", "1")
            .WithEnvironment("Organization__InvitationKeys__Keys__1", secrets.InvitationKey);
    }

    databases.Add(cell.DatabaseName, cellMigrate, cellApp, cellWorker);
    databases.AddRelay(cell.DatabaseName, cellWorker);
    databases.AddMaintenance(cell.DatabaseName, cellWorker);
}

await builder.Build().RunAsync();
