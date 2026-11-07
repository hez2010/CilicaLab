# Cilica Lab

A C# playground for the Cilica optimizer. Compile a snippet, inspect each method's intermediate representation before and after every Cilica pass, view its control-flow graphs, and compare the original and generated IL or decompiled C#.

Cilica Lab runs entirely in Blazor WebAssembly. Deployment consists of static files; compilation does not use a backend. The optimizer comes from the [Cilica.Core](https://www.nuget.org/packages/Cilica.Core/0.18.0-preview) NuGet package, independently of a Cilica source checkout.

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

## Explore a compilation

1. Choose an example or edit the C# source. The editor keeps your latest source in browser local storage when storage is available.
2. Select **Compile & optimize**, or press **Ctrl+Enter** (**Cmd+Enter** on macOS) in the editor.
3. Choose a method, including an overload, constructor, or compiler-generated method.
4. Select a pass to compare its input and output IR. Each invocation appears separately, including repeated passes and passes that leave the method unchanged. **Changed only** filters the timeline. Choose **Graphviz CFG** to render the control-flow graphs before and after that pass.
5. Open **Generated IL** to compare Roslyn's IL with Cilica's emitted IL, or **ILSpy C#** to compare decompiled C# for the original and optimized assemblies. ILSpy decompilation runs on demand and covers the whole assembly, exposing compiler-generated types, backing fields, and state machines. **Download DLL** saves the resulting assembly.

**Split view** switches between side-by-side and inline code diffs. Drag the divider between the source and results to resize the desktop workspace, or focus it and use the Left/Right arrow keys. Home and End move it to its minimum and maximum positions.

Each control-flow graph has independent zoom controls and a **Fit** button. Ctrl+wheel or Cmd+wheel zooms around the pointer, and scrollbars let you explore larger graphs. Graphs fit their panes automatically until you choose a manual zoom level.

**Options** controls Roslyn's Release optimization, Cilica method inlining, and cross-assembly optimization. Edits and option changes mark the displayed results as outdated until the next compilation. Source diagnostics appear in the editor. If Cilica cannot transform a method, its diagnostic is shown and its original IL is retained in the output assembly.

The examples cover algebraic simplification, inlining, loop vectorization, LINQ expansion, scalar replacement, and repeated loads.

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

### GitHub Pages

The [Build and deploy workflow](.github/workflows/pages.yml) builds pull requests and publishes pushes to `main` at [hez2010.github.io/CilicaLab](https://hez2010.github.io/CilicaLab/). It can also be started manually from GitHub Actions. The repository's Pages publishing source is **GitHub Actions**; deployment uses the built-in GitHub token and the `github-pages` environment.

## Scope

- C# 14 with the bundled .NET 10 framework assemblies. Additional NuGet references are not supported in snippets.
- Code generation displays managed IL and ILSpy's reconstructed C#. Native JIT assembly is not available in this browser app.
- Snippets are compiled and optimized, but are not executed.
- Compilation runs on the browser's UI thread. Large snippets or expensive optimization passes can temporarily make the interface unresponsive.
- Cilica is experimental. Failed or unsupported methods retain their original IL, while other methods can still be optimized.

## Projects

- `src/CilicaLab`: the standalone Blazor WebAssembly app and Monaco interface.
- `src/CilicaLab.Compiler`: Roslyn compilation, Cilica pass inspection, IL formatting, and ILSpy decompilation.

## Acknowledgements

Cilica Lab uses [Cilica](https://github.com/hez2010/Cilica), [Roslyn](https://github.com/dotnet/roslyn), [ILSpy](https://github.com/icsharpcode/ILSpy), Monaco assets supplied by [BlazorMonaco](https://github.com/serdarciplak/BlazorMonaco), and [Viz.js](https://viz-js.com/) for Graphviz rendering.
