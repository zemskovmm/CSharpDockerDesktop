# UI rendering

The application uses C#/.NET 10 and Uno Platform Skia Desktop. The production
and graphical smoke hosts request the same platform rendering configuration:

| Delivery target | Preferred backend | Automatic fallback |
| --- | --- | --- |
| Windows x64, Win32 host | Vulkan through Skia | OpenGL, then software |
| macOS Apple Silicon and Intel, AppKit host | Metal through Skia | Software |

The Windows host selects `Win32RenderingBackend.Vulkan` through Uno's host
builder. This is a preference with fallback in the pinned Uno version, not a
requirement for a Vulkan-capable system. The macOS host leaves renderer selection
at its default so that Metal is preferred without disabling software fallback.
No custom renderer, additional graphics package, SDK upgrade, or Vulkan-to-Metal
translation layer is introduced. Linux remains outside the delivery scope in
IDD-0001.

[IDD-0003](../.idd/intent/IDD-0003.spec-shared-resource-ui.md) owns shared UI
availability and automatic fallback acceptance.
[ENG-0001](../.idd/engineering/ENG-0001.rule-desktop-stack.md) owns the selected
technology stack and backends. ENG-0002 continues to keep Docker behavior and
transport independent of presentation. Navigation, resource operations,
confirmation, keyboard behavior, and stored preferences retain their existing
contracts.

## Upstream compatibility evidence

The repository pins Uno.Sdk 6.7.30, resolving Uno runtime packages 6.7.135.
The corresponding upstream source revision is
`da217df2312e9e39676880f9cb4b8d519cc297e0`:

- [Win32 host builder](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI.Runtime.Skia.Win32/Builder/Win32HostBuilder.cs)
  maps the Vulkan preference to Uno's graphics configuration.
- [Win32 window initialization](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Window/Win32WindowWrapper.cs)
  attempts Vulkan, OpenGL, and software in that order.
- [macOS host initialization](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI.Runtime.Skia.MacOS/Hosting/MacSkiaHost.cs)
  negotiates Metal and software when the Metal flag is left unset.

These establish configuration and fallback implementation evidence, not an
observed Windows GPU run. Uno's [Vulkan documentation](https://platform.uno/docs/articles/features/using-vulkan-rendering.html)
also describes the supported backends and fallback behavior.

## Verification on 2026-10-16

Host: Apple Silicon, macOS 26.7.1, .NET SDK 10.0.402. No
`.idd/verification.yaml` exists; checks use the repository's solution build,
executable regression harness, graphical smoke host, and publish profile.

- Release solution build: passed, zero warnings and errors.
- Existing executable regression harness: **414/414 groups passed**.
- Release graphical smoke host build: passed, zero warnings and errors.
  The initial no-restore attempt encountered stale development-package assets;
  a normal Release restore/build resolved them without a source change.
- Actual Uno/macOS graphical checks: both runtime-startup groups passed.
  These cover loaded startup/cancel controls, visible destination, duplicate
  admission, navigation during launch, and showing readiness only after a
  negotiated connection. They use synthetic Application fixtures and do not
  start a real runtime or mutate Docker resources.
- Windows x64 self-contained cross-publish: passed to
  `artifacts/rendering-check/win-x64/`.
- Engineering structure, intent ownership/index consistency, and diff whitespace
  checks: passed.

The focused audit covers IDD-0003's rendering availability and the preserved
Windows/macOS scope from IDD-0001, with ENG-0001 and ENG-0002. Configuration
matches the updated requirements; no implementation drift was found in that
scope. Remaining evidence gaps are listed below.

## Remaining platform checks

On actual Windows systems, record the selected renderer and verify launch,
keyboard navigation, resize, Engine recovery, and a large resource list with:

1. A usable Vulkan GPU and driver.
2. Vulkan unavailable, allowing automatic OpenGL fallback.
3. GPU backends unavailable, allowing software fallback.

Do not infer those results from the Mac regression suite or Windows publish.
The Mac graphical checks establish usable controls on AppKit, but do not
independently measure the selected GPU backend, performance budgets, Intel Mac
runtime behavior, or macOS software fallback. Those remain unverified.
