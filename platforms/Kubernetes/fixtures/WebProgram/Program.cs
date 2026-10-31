using System;
using System.Net.Http;
using System.Text;

using Assimalign.Cohesion;
using Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes;
using Assimalign.Cohesion.ApplicationModel.Kubernetes.Fixtures;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Hosting.ApplicationModel;

// The resource's program as its Program.cs would be: it makes itself a resource on the Kubernetes
// platform, with an action of its own, and answers with its name, or, when its Orders:Url setting
// arrived, with its name and what that address answers. Nothing in it knows the cluster.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string resource = AppEnvironment.GetValue(AppEnvironment.Variables.Resource) ?? "orders-api";
string? orders = builder.Configuration["Orders:Url"];
builder.AddResource(resource, options => options
    .UseKubernetes()
    .AddAction(new GreetingAction(), GreetingJsonContext.Default));

WebApplication application = builder.Build();
using HttpClient? client = orders is null ? null : new HttpClient { BaseAddress = new Uri(orders) };
application.Use(async (context, _) =>
{
    string answer = client is null ? resource : $"{resource} -> {await client.GetStringAsync("/")}";
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(answer));
});

await application.RunAsync();
