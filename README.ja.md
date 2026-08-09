# YetAnotherAnalyzerProfiler (YAAP)

[English](README.md) | 日本語

> [!NOTE]
> これは [英語版README](README.md) の公式日本語訳です。英語版を正本とし、内容が異なる場合は英語版を優先します。

[![CI](https://github.com/furbon/YetAnotherAnalyzerProfiler/actions/workflows/ci.yml/badge.svg?branch=main&event=push)](https://github.com/furbon/YetAnotherAnalyzerProfiler/actions/workflows/ci.yml?query=branch%3Amain)
[![NuGet](https://img.shields.io/nuget/v/YetAnotherAnalyzerProfiler.Tool?logo=nuget)](https://www.nuget.org/packages/YetAnotherAnalyzerProfiler.Tool)
[![GitHub Release](https://img.shields.io/github/v/release/furbon/YetAnotherAnalyzerProfiler?display_name=tag&sort=semver)](https://github.com/furbon/YetAnotherAnalyzerProfiler/releases/latest)

YAAPは、C#ビルドに含まれるRoslyn AnalyzerとSource Generatorのコストをコンパイラー報告値から測定する、ローカル実行型のプロファイラーです。測定結果を履歴として保存し、2回の測定の比較とCSV／JSON／Markdown出力を行えます。

- Windows、macOS、Linuxで利用できる非対話CLI
- Windows向けのWPF GUI
- `.sln`、`.slnx`、`.csproj`と.NET 8／10 SDKに対応
- AnalyzerとSource Generatorを分離して集計
- 生成ファイルの件数、サイズ、行数、相対パスを記録
- ローカル履歴、検索、比較、エクスポート

> [!WARNING]
> YAAPはサンドボックスではありません。測定時には対象のrestore、clean、buildに加え、Analyzer／Source Generatorを含むコンパイラー呼び出しを再実行します。対象コードはYAAPと同じユーザー権限でファイル操作、プロセス起動、通信などの副作用を起こせます。信頼できないリポジトリを実行しないでください。`--isolated`は`bin`／`obj`の出力先を分ける機能であり、セキュリティ境界ではありません。詳しくは[セキュリティ方針](docs/ja/security.md#測定対象との信頼境界)を参照してください。

## クイックスタート

NuGetのパッケージページと由来証明付きリリースを確認したうえで、最新の安定版CLIを.NETグローバルツールとしてインストールします。

```powershell
dotnet tool install --global YetAnotherAnalyzerProfiler.Tool
yaap version
```

リポジトリからCLIを実行する例です。

```powershell
dotnet run --project src/Yaap.Cli --framework net10.0 -- profile path/to/App.slnx
dotnet run --project src/Yaap.Cli --framework net10.0 -- history list
```

WindowsではGUIも起動できます。

```powershell
dotnet run --project src/Yaap.Gui --framework net10.0-windows
```

分離出力は既定で有効です。.NETの`--artifacts-path`をrestore、clean、buildへ渡しますが、カスタムMSBuild targetなどによる任意の書き込みまでは防止しません。対象の標準`bin`／`obj`を使う場合だけ`--no-isolated`を指定します。YAAP自身にテレメトリや更新確認はありませんが、対象のrestore、build、Analyzer、Source Generatorは対象の構成や実装に従って通信する可能性があります。

## 配布物

ローカルで自己完結型バイナリを作成する例です。

```powershell
./eng/build.ps1 publish --runtime win-x64 --framework net10.0
```

```sh
./eng/build.sh publish --runtime linux-x64 --framework net10.0
```

出力は`artifacts/publish/<RID>/<TFM>/`の`cli`、Windowsでは`gui`に作成されます。`Yaap.BuildLogger.dll`は測定に必要なため、実行ファイルと同じディレクトリに保持してください。正式なリリース用アーカイブには、利用対象の実行ファイルに加えて`LICENSE`、`THIRD-PARTY-NOTICES.txt`、README、CHANGELOGを含めます。

CLIのNuGetパッケージをローカルで作成・インストール検証する場合は、次を実行します。

```powershell
./eng/build.ps1 pack --framework net10.0
```

検証済みパッケージは`artifacts/packages/YetAnotherAnalyzerProfiler.Tool.<version>.nupkg`に作成されます。

配布アーカイブを展開した後は、SDKプロジェクトを開かずに実行できます。Windowsでは次のように起動します。

```powershell
.\cli\yaap.exe version
.\cli\yaap.exe profile C:\path\to\App.slnx
.\gui\yaap-gui.exe
```

Linux／macOSのCLIは、展開先で実行権限を確認して起動します。

```sh
chmod +x ./cli/yaap
./cli/yaap version
./cli/yaap profile /path/to/App.slnx
```

ソースは.NET 8／10を対象にします。公開する自己完結型バイナリのOS、CPUアーキテクチャ、TFMは、各[リリース](https://github.com/furbon/YetAnotherAnalyzerProfiler/releases)と[変更履歴](CHANGELOG.md)で確認してください。

## CLIとGUI

CLIとGUIは同じCoreを使用し、測定条件、履歴フィルター、既存binlog解析、比較、全件exportを両方で利用できます。標準出力、ファイル選択、テーマなど表示媒体に応じた操作差は、[機能対応表](docs/ja/index.md#cliとguiの機能対応表)に明記しています。

## データとプライバシー

履歴には測定値のほか、対象の絶対パス、SDK／OS情報、Git情報、診断、失敗またはキャンセルした子プロセスの完全ログ、binlogが含まれ得ます。binlogやrestore／clean／build出力には、対象プロジェクト由来の機密情報が含まれる可能性があります。

履歴はローカルに保存され、YAAP自身は送信しません。共有前に内容を確認し、不要な履歴は削除してください。

## ドキュメント

最初に[日本語ドキュメントガイド](docs/ja/index.md)を参照してください。日本語版のない文書は英語版へ案内します。

- [利用ガイド](docs/ja/usage.md)
- [測定設計](docs/ja/measurement.md)
- [トラブルシュート](docs/ja/troubleshooting.md)
- [セキュリティ方針](docs/ja/security.md)
- [サポート方針](docs/ja/support.md)
- [英語版ドキュメント一覧](docs/index.md)

## ライセンス

YAAPは[MIT License](LICENSE)で提供します。配布に含まれる第三者ソフトウェアは[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)を参照してください。
