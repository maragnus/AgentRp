var builder = DistributedApplication.CreateBuilder(args);

var app = builder.AddProject<Projects.AgentRp>("app");

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
