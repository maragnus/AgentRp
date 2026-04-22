var builder = DistributedApplication.CreateBuilder(args);

var openAiApiKey = builder.AddParameter("openai-api-key", secret: true);
var openAiModel = builder.AddParameter("openai-model");
var customName = builder.AddParameter("custom-name");
var customEndpoint = builder.AddParameter("custom-endpoint");
var customApiKey = builder.AddParameter("custom-api-key", secret: true)
    .WithDescription("API key for the custom endpoint (optional)");

var app = builder.AddProject<Projects.AgentRp>("app")
    .WithEnvironment("Agents__0__ApiKey", openAiApiKey)
    .WithEnvironment("Agents__0__Model", openAiModel)
    .WithEnvironment("Agents__1__Name", customName)
    .WithEnvironment("Agents__1__EndPoint", customEndpoint)
    .WithEnvironment("Agents__1__ApiKey", customApiKey);

var database = builder.AddAzureSqlServer("agentrp-sql")
    .RunAsContainer(c => c
        .WithContainerName("agentrp-sql")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume()
        .WithContainerRuntimeArgs("-e", "ACCEPT_EULA=1", "--cap-add", "SYS_PTRACE")
        .WithImage("azure-sql-edge"))
    .AddDatabase("agentrp-db", "agentrp");

app.WaitFor(database).WithReference(database);

builder.Build().Run();
