# Cilica Lab

A C# playground for the Cilica optimizer. Compile a snippet, inspect each method's intermediate representation before and after every Cilica pass, read the trace log each pass records for that method, view its control-flow graphs, and compare the original and generated IL or decompiled C#.

Cilica Lab runs entirely in Blazor WebAssembly. Deployment consists of static files; compilation does not use a backend. The optimizer comes from the [Cilica.Core](https://www.nuget.org/packages/Cilica.Core) NuGet package.

## Run locally

Install the .NET 10 SDK, then run from the repository root:

```sh
dotnet run --project src/CilicaLab
```

Open `http://localhost:5188`. The development host serves the app's static assets; Roslyn and Cilica run in your browser. The first load downloads the compiler and .NET assemblies.

For hot reload:

```sh
dotnet watch --project src/CilicaLab run
```

## Publish as a static site

```sh
dotnet publish src/CilicaLab -c Release -o artifacts/site
```

Deploy the contents of `artifacts/site/wwwroot` to a static HTTP host. Keep the `_framework`, `_content`, and `references` directories alongside `index.html`. The relative base URL supports hosting under a subdirectory such as `/CilicaLab/`; open the directory URL with its trailing slash.

To preview the published files locally with [dotnet-serve](https://github.com/natemcmaster/dotnet-serve):

```sh
dotnet tool install --global dotnet-serve
dotnet serve -d artifacts/site/wwwroot -p 5189 -b -z
```

The host must serve `.wasm` as `application/wasm`, JavaScript as JavaScript, and `.zip` as a binary file. No application server, API routes, database, or secrets are required. Opening `index.html` directly through `file://` is not supported by Blazor WebAssembly.

## Projects

- `src/CilicaLab`: the standalone Blazor WebAssembly app and Monaco interface.
- `src/CilicaLab.Compiler`: Roslyn compilation, Cilica pass inspection and trace capture, IL formatting, and ILSpy decompilation.

## Acknowledgements

Cilica Lab uses [Cilica](https://github.com/hez2010/Cilica), [Roslyn](https://github.com/dotnet/roslyn), [ILSpy](https://github.com/icsharpcode/ILSpy), Monaco assets supplied by [BlazorMonaco](https://github.com/serdarciplak/BlazorMonaco), and [Viz.js](https://viz-js.com/) for Graphviz rendering.
