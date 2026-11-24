using Backroom.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var services = builder.AddDevelopmentServices();
var keycloak = services.Keycloak;
var databases = new DevelopmentDatabases(builder, services.Postgres);

// Services validate tokens against the realm; its URL is also the token issuer.
var authority = ReferenceExpression.Create(
    $"{keycloak.GetEndpoint("http").Property(EndpointProperty.Url)}/realms/{DevelopmentServices.KeycloakRealm}");

// The development cell's one-time registration key (docs/contracts/phase-1.md, "Cell registration"):
// the control plane accepts it for that cell, and the cell's worker registers with it on first start.
var cellRegistrationKey = builder.AddParameter(
    "cell-a-registration-key",
    new GenerateParameterDefault { MinLength = 43, Special = false },
    secret: true,
    persist: true);

// The development cell's invitation key, version 1 (docs/contracts/phase-1.md, "Invitation security"). The
// cell reads it as base64: 44 letters and digits are valid base64 of 33 bytes.
var cellInvitationKey = builder.AddParameter(
    "cell-a-invitation-key",
    new GenerateParameterDefault { MinLength = 44, Special = false },
    secret: true,
    persist: true);

// The control plane's key for invited emails (docs/contracts/phase-1.md, "Data"); read as base64, like the cell's key.
var emailHmacKey = builder.AddParameter(
    "control-plane-email-hmac-key",
    new GenerateParameterDefault { MinLength = 44, Special = false },
    secret: true,
    persist: true);

// The development cell's key for the recipient hashes in its email delivery log (docs/contracts/phase-1.md,
// "Data"); read as base64, like the invitation key.
var cellEmailRecipientHashKey = builder.AddParameter(
    "cell-a-email-recipient-hash-key",
    new GenerateParameterDefault { MinLength = 44, Special = false },
    secret: true,
    persist: true);

// Control plane. Its migrate run starts first, because it also creates the shared development roles.
var controlPlaneMigrate = builder.AddProject<Projects.Backroom_ControlPlane_Host>(
        "control-plane-migrate", options => options.ExcludeLaunchProfile = true)
    .WithArgs("migrate");

var controlPlane = builder.AddProject<Projects.Backroom_ControlPlane_Host>("control-plane")
    .WithEnvironment("Authentication__Authority", authority)
    .WithEnvironment("CellManagement__Keycloak__AdminClientSecret", services.ControlPlaneAdminSecret)
    .WithEnvironment("Features__OrganizationCreation", "true")
    .WithWebOrigins()
    .WithEnvironment("Directory__Invitations__EmailHmacKey", emailHmacKey)
    .WithEnvironment("CellManagement__DevelopmentCells__0__Id", DevelopmentCells.First)
    .WithEnvironment("CellManagement__DevelopmentCells__0__Name", "Development cell A")
    .WithEnvironment("CellManagement__DevelopmentCells__0__RegistrationKey", cellRegistrationKey)
    .WaitFor(keycloak)
    .WithHttpHealthCheck("/health");

// Keycloak ends a session by calling the control plane (OIDC Back-Channel Logout, docs/contracts/phase-1.md, flow 5). It runs in a
// container and the control plane on the host, so it reaches it through the container host name, which the control plane must
// accept (AllowedHosts). Plain HTTP: the container does not trust the development certificate; deployments use https.
keycloak.WithEnvironment(
    "BACKROOM_BACKCHANNEL_LOGOUT_URL",
    ReferenceExpression.Create($"{controlPlane.GetEndpoint("http").Property(EndpointProperty.Url)}/internal/keycloak/backchannel-logout"));
controlPlane.WithEnvironment("AllowedHosts", "localhost;127.0.0.1;[::1];host.docker.internal");

databases.Add("control_plane", controlPlaneMigrate, controlPlane);

// One cell for now: the same binary runs as the "migrate", "app" and "worker" role.
// The role is set explicitly; launch profiles only supply ports.
var cellMigrate = builder.AddProject<Projects.Backroom_Cell_Host>(
        "cell-a-migrate", options => options.ExcludeLaunchProfile = true)
    .WithEnvironment("Cell__Role", "migrate")
    .WaitForCompletion(controlPlaneMigrate);

var cellApp = builder.AddProject<Projects.Backroom_Cell_Host>("cell-app", launchProfileName: "app")
    .WithEnvironment("Cell__Id", DevelopmentCells.First)
    .WithEnvironment("Cell__Role", "app")
    .WithWebOrigins()
    .WithEnvironment("Authentication__Authority", authority)
    // Live events: the SignalR backplane and the member-removal channel (docs/contracts/phase-1.md, "Live events"). Cells share the
    // development instance and are separated by a channel prefix.
    .WithEnvironment("Valkey__ConnectionString", services.Valkey.Resource.ConnectionStringExpression)
    .WaitFor(keycloak)
    .WaitFor(services.Valkey)
    .WithHttpHealthCheck("/health");

// The worker registers the cell with the control plane on its first start, then keeps its link
// (gRPC over HTTP/2, so it uses the https endpoint). It also relays the modules' outboxes to RabbitMQ and
// runs their event consumers; the app role only writes outboxes, so only the worker gets RabbitMQ.
var cellWorker = builder.AddProject<Projects.Backroom_Cell_Host>("cell-worker", launchProfileName: "worker")
    .WithEnvironment("Cell__Id", DevelopmentCells.First)
    .WithEnvironment("Cell__Role", "worker")
    .WithEnvironment("Authentication__Authority", authority)
    .WithEnvironment("ControlPlaneConnector__ControlPlaneUrl", controlPlane.GetEndpoint("https"))
    .WithEnvironment("ControlPlaneConnector__RegistrationKey", cellRegistrationKey)
    .WithEnvironment("RabbitMq__ConnectionString", services.RabbitMq.Resource.ConnectionStringExpression)
    .WithEnvironment("RabbitMq__ClientName", "cell-a-worker")
    // Only the worker sends email, and only to Mailpit (docs/adr/0008); the app role gets no SMTP endpoint.
    .WithEnvironment("Email__Mailpit__Host", services.Mailpit.Resource.Host)
    .WithEnvironment("Email__Mailpit__Port", services.Mailpit.Resource.Port)
    .WithEnvironment("Email__FromAddress", "no-reply@backroom.test")
    .WithEnvironment("Email__FromName", "Backroom")
    .WithEnvironment("Email__RecipientHashKey", cellEmailRecipientHashKey)
    // Invitation emails link to the web app's dev server (frontend/apps/web, fixed port); http only on loopback in Development.
    .WithEnvironment("Organization__InvitationLinks__WebOrigin", "http://localhost:5173")
    .WaitFor(keycloak)
    .WithEnvironment("Valkey__ConnectionString", services.Valkey.Resource.ConnectionStringExpression)
    .WaitFor(services.RabbitMq)
    .WaitFor(services.Valkey)
    .WaitFor(services.Mailpit)
    .WaitFor(controlPlane)
    .WithHttpHealthCheck("/health");

foreach (var cell in (IResourceBuilder<ProjectResource>[])[cellApp, cellWorker])
{
    cell.WithEnvironment("Organization__InvitationKeys__CurrentVersion", "1")
        .WithEnvironment("Organization__InvitationKeys__Keys__1", cellInvitationKey);
}

databases.Add("cell_a", cellMigrate, cellApp, cellWorker);
databases.AddRelay("cell_a", cellWorker);
databases.AddMaintenance("cell_a", cellWorker);

await builder.Build().RunAsync();
