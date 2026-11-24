using Backroom.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var cells = DevelopmentCells.Selected(builder.Configuration);
var services = builder.AddDevelopmentServices();
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
    .WithEnvironment("Directory__Invitations__EmailHmacKey", emailHmacKey)
    .WaitFor(keycloak)
    .WithHttpHealthCheck("/health");

// The shared development cells, seeded as pending cells; the public URL is the cell's app endpoint, which the web app
// learns from /api/v1/cells/shared and the organization data.
for (var i = 0; i < cells.Count; i++)
{
    controlPlane
        .WithEnvironment($"CellManagement__DevelopmentCells__{i}__Id", cells[i].Id)
        .WithEnvironment($"CellManagement__DevelopmentCells__{i}__Name", cells[i].Name)
        .WithEnvironment($"CellManagement__DevelopmentCells__{i}__PublicUrl", cells[i].PublicUrl.ToString())
        .WithEnvironment($"CellManagement__DevelopmentCells__{i}__RegistrationKey", cellSecrets[cells[i].Id].RegistrationKey);
}

// Keycloak ends a session by calling the control plane (OIDC Back-Channel Logout, docs/contracts/phase-1.md, flow 5). It runs in a
// container and the control plane on the host, so it reaches it through the container host name, which the control plane must
// accept (AllowedHosts). Plain HTTP: the container does not trust the development certificate; deployments use https.
keycloak.WithEnvironment(
    "BACKROOM_BACKCHANNEL_LOGOUT_URL",
    ReferenceExpression.Create($"{controlPlane.GetEndpoint("http").Property(EndpointProperty.Url)}/internal/keycloak/backchannel-logout"));
controlPlane.WithEnvironment("AllowedHosts", "localhost;127.0.0.1;[::1];host.docker.internal");

databases.Add("control_plane", controlPlaneMigrate, controlPlane);

// Cells share the development PostgreSQL server, Valkey and RabbitMQ (docs/contracts/phase-1.md, "Development topology"):
// a database each, a virtual host each, and a Valkey key and channel prefix from the cell id. The roles are server-wide, so
// all databases use the same role passwords. In production each cell has its own instances.
var virtualHosts = builder.AddRabbitMqVirtualHosts(services.RabbitMq, cells);

foreach (var cell in cells)
{
    var secrets = cellSecrets[cell.Id];

    // The same binary runs as the "migrate", "app" and "worker" role.
    // The role is set explicitly; launch profiles only supply ports.
    var cellMigrate = builder.AddProject<Projects.Backroom_Cell_Host>(
            $"cell-{cell.Id}-migrate", options => options.ExcludeLaunchProfile = true)
        .WithEnvironment("Cell__Role", "migrate")
        .WaitForCompletion(controlPlaneMigrate);

    var cellApp = builder.AddProject<Projects.Backroom_Cell_Host>($"cell-{cell.Id}-app", launchProfileName: "app" + cell.LaunchProfileSuffix)
        .WithEnvironment("Cell__Id", cell.Id)
        .WithEnvironment("Cell__Role", "app")
        .WithEnvironment("Authentication__Authority", authority)
        // Live events: the SignalR backplane and the member-removal channel (docs/contracts/phase-1.md, "Live events"),
        // separated per cell by the prefix the cell derives from its id.
        .WithEnvironment("Valkey__ConnectionString", services.Valkey.Resource.ConnectionStringExpression)
        .WaitFor(keycloak)
        .WaitFor(services.Valkey)
        .WithHttpHealthCheck("/health");

    // The worker registers the cell with the control plane on its first start, then keeps its link
    // (gRPC over HTTP/2, so it uses the https endpoint). It also relays the modules' outboxes to RabbitMQ and
    // runs their event consumers; the app role only writes outboxes, so only the worker gets RabbitMQ.
    var cellWorker = builder.AddProject<Projects.Backroom_Cell_Host>($"cell-{cell.Id}-worker", launchProfileName: "worker" + cell.LaunchProfileSuffix)
        .WithEnvironment("Cell__Id", cell.Id)
        .WithEnvironment("Cell__Role", "worker")
        .WithEnvironment("Authentication__Authority", authority)
        .WithEnvironment("ControlPlaneConnector__ControlPlaneUrl", controlPlane.GetEndpoint("https"))
        .WithEnvironment("ControlPlaneConnector__RegistrationKey", secrets.RegistrationKey)
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
