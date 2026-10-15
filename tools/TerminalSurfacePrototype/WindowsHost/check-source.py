#!/usr/bin/env python3
"""Focused source wiring checks for the exact pinned host patch; no Windows execution implied."""
from pathlib import Path
import sys

root = Path(sys.argv[1]) / 'src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Controls/WebView'

def check(source, backend):
    constructor = source[source.index(f'public Win32Native{backend}WebView('):source.index('\n\tprivate void ForwardBackgroundToPresenter()')]
    create = 'CreateCoreWebView2Environment(userDataFolder, options,' if backend == 'Aot' else 'CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder, options: options)'
    configure = f'TerminalResearchWebViewPolicy.Create{"Aot" if backend == "Aot" else "Managed"}Options()'
    stages = ['TerminalResearchWebViewPolicy.RequireRuntime(', 'TerminalResearchWebViewPolicy.ClaimUserDataFolder()', configure, create]
    assert all(constructor.count(stage) == 1 for stage in stages), 'Missing or duplicate creation/configuration path'
    assert [constructor.index(stage) for stage in stages] == sorted(constructor.index(stage) for stage in stages), 'Options/runtime validation must precede creation'
    assert 'catch (Exception e)' in constructor and 'tcs.TrySetException(e)' in constructor, 'Failures must complete initialization as failed'
    track = f'TerminalResearchWebViewPolicy.Track{"Aot" if backend == "Aot" else "Managed"}Environment('
    controller = 'environment.CreateCoreWebView2Controller(' if backend == 'Aot' else 'env.CreateCoreWebView2ControllerAsync('
    assert constructor.index(track) < constructor.index(controller), 'Environment must be validated/tracked before controller creation'
    assert 'TerminalResearchWebViewPolicy.TrackController(' in constructor, 'Controller must participate in joined cleanup'

for name, backend in [('Win32NativeAotWebView.cs', 'Aot'), ('Win32NativeWebView.cs', '')]:
    source = (root / name).read_text(encoding='utf-8-sig')
    check(source, backend)
    # Negative regressions: a missing option supplier and creation moved ahead of configuration both reject.
    for modified in [source.replace('TerminalResearchWebViewPolicy.CreateAotOptions()', 'new WebView2.CoreWebView2EnvironmentOptions()') if backend else source.replace('TerminalResearchWebViewPolicy.CreateManagedOptions()', 'new NativeWebView.CoreWebView2EnvironmentOptions()'),
                     source.replace('TerminalResearchWebViewPolicy.RequireRuntime(', 'RemovedRuntimeCheck(')]:
        try:
            check(modified, backend)
        except AssertionError:
            pass
        else:
            raise AssertionError('Unsafe mutation was accepted')
managed = (root / 'Win32NativeWebView.cs').read_text()
assert 'throw new InvalidOperationException("Unsupported research WebView2 backend; no fallback is allowed.")' in managed
assert 'Required research WebView2 backend unavailable; no fallback is allowed.' in managed
policy = Path(__file__).with_name('TerminalResearchWebViewPolicy.cs').read_text()
assert 'put_IsCustomCrashReportingEnabled(BOOL.TRUE).ThrowOnError()' in policy
assert 'get_IsCustomCrashReportingEnabled(ref custom).ThrowOnError()' in policy
assert 'IsCustomCrashReportingEnabled = true' in policy
assert '!options.IsCustomCrashReportingEnabled' in policy
program = Path(__file__).parent.parent.joinpath('Program.cs').read_text()
assert program.index('WindowsResearchHost.Prepare()') < program.index('UnoPlatformHostBuilder.Create()')
print('Pinned source checks passed: both creation paths, option propagation/order, failure completion, no unknown-backend fallback, controller tracking and startup gate; unsafe mutations rejected.')
