using KubeOps.Operator;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddKubernetesOperator()
    .RegisterComponents();

using IHost host = builder.Build();
await host.RunAsync();