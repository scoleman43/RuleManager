# Copilot instructions

## Build and run

Run these commands from the repository root:

```sh
dotnet build RuleManager.sln
dotnet test RuleManager.sln
dotnet run --project RuleManager.Web/RuleManager.Web.csproj
```

To run one xUnit test, filter by its fully qualified name:

```sh
dotnet test RuleManager.Tests/RuleManager.Tests.csproj --filter "FullyQualifiedName~Namespace.ClassName.TestMethod"
```

## Architecture

- `RuleManager.sln` contains four .NET 8 projects. Nullable reference types and implicit usings are enabled.
- `RuleManager.Core` is the shared class library. `RuleManager.Data` references Core; `RuleManager.Web` references both Core and Data.
- `RuleManager.Web` is an ASP.NET Core Blazor Web App. `Program.cs` configures Razor Components with Interactive Server support and maps the root `App` component. Routes, layouts, and pages live under `Components/`; static assets live under `wwwroot/`.
- `RuleManager.Tests` uses xUnit and currently references Core. Keep tests for Core behavior in this project; update project references if tests need another layer.
- The current source is primarily the Blazor starter scaffold. Treat existing sample pages and placeholder classes as scaffolding, not as established rule-management or persistence behavior.

## Conventions

- Keep code in the project matching its layer and preserve the existing project-reference direction: Data depends on Core, while Web composes Core and Data.
- Use the project-root namespaces (`RuleManager.Core`, `RuleManager.Data`, `RuleManager.Web`, and `RuleManager.Tests`) and place routed UI components in `RuleManager.Web/Components/Pages` with their routes declared using `@page`.
- Register application services in the Web host's `Program.cs`; use Razor component dependency injection for UI consumers.
