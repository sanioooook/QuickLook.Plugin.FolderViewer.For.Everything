# QuickLook.Plugin.FolderViewer.For.Everything

A fast folder preview plugin for [QuickLook](https://github.com/QL-Win/QuickLook).

> [!IMPORTANT]
> 此项目为 AI 二次开发版本，新增及修改的代码均由 OpenAI GPT-5.6 Sol 通过 Codex 生成并迭代完善。
> This is an AI-modified fork. All newly introduced and modified code was generated and iteratively refined by OpenAI GPT-5.6 Sol through Codex.
> See [AI_MODIFICATION_NOTICE.md](AI_MODIFICATION_NOTICE.md) for the full bilingual disclosure and comparison with the original plugin.

## What changed

- The preview reads only the selected folder. Subfolders are loaded when expanded.
- Native `FindFirstFileEx` enumeration gets names, attributes, sizes, and dates in one pass.
- WPF tree virtualization and a 25,000-item per-folder preview guard keep wide folders responsive.
- File icons are cached by extension and never invoke per-file thumbnail handlers.
- Recursive statistics run separately at low priority, can be cancelled, and skip reparse points.
- Network folders never trigger an automatic recursive scan.
- Everything 1.5 indexed size is queried through the SDK3 named-pipe protocol.
- Everything 1.4/1.5 `WM_COPYDATA` IPC supplies indexed size and descendant counts as a fallback.
- Errors, cancellation, cleanup, long paths, GDI resource handling, and sorting collisions are handled.

The directory tree always comes from the filesystem. Everything is an optional aggregate-statistics
accelerator, so a stale or incomplete index cannot hide files from the preview.

## Everything acceleration

Everything is optional. For the fastest total size and recursive file/folder counts:

1. Install the full edition of [Everything](https://www.voidtools.com/downloads/). The Lite edition has no IPC support.
2. Keep Everything running and enable IPC.
3. In Everything, open **Tools > Options > Indexes > Size**.
4. Enable **Index file size** and **Index folder size**.

Everything `1.5.0.1384a` or newer uses the fastest named-pipe folder-size query. Older 1.4/1.5
versions use a bounded window-IPC query. If the index is unavailable, local statistics fall back to
a cancellable background scan without delaying the folder tree.

## Install

Download the `.qlplugin` release, select it in File Explorer, press Space, and restart QuickLook when
prompted. The standard plugin instructions are available in the
[QuickLook wiki](https://github.com/QL-Win/QuickLook/wiki/Available-Plugins#how-to-install-or-upgrade-a-plugin).

## Build and test

Requirements:

- Windows
- Visual Studio with MSBuild and the .NET Framework 4.6.2 targeting pack
- Git submodules initialized

```powershell
git submodule update --init --recursive
$env:FOLDERVIEWER_VERSION = "2.0.0"
msbuild QuickLook.Plugin.FolderViewer.For.Everything.sln /t:Rebuild /p:Configuration=Release /p:Platform="Any CPU"
./Tests/bin/Release/QuickLook.Plugin.FolderViewer.Tests.exe
./Scripts/pack-zip.ps1
Remove-Item Env:FOLDERVIEWER_VERSION
```

QuickLook supplies `QuickLook.Common` at runtime; it must not be included in the `.qlplugin` archive.

## License

See `LICENSE.txt`, the per-file notices retained from the original QuickLook sources, and
`THIRD_PARTY_NOTICES.txt` for the Everything SDK protocol definitions.
