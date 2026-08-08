# YetAnotherAnalyzerProfiler (YAAP)

YAAP は、C# ビルドに含まれる Roslyn Analyzer と Source Generator のコストを
コンパイラー報告値から測定する、ローカル実行型のプロファイラーです。測定結果を履歴として保存し、
2回の測定の比較と CSV／JSON／Markdown 出力を行えます。

- Windows、macOS、Linux で利用できる非対話 CLI
- Windows 向けの WPF GUI
- `.sln`、`.slnx`、`.csproj` と .NET 8／10 SDK に対応
- Analyzer と Source Generator を分離して集計
- 生成ファイルの件数、サイズ、行数、相対パスを記録
- ローカル履歴、検索、比較、エクスポート

> [!WARNING]
> YAAP はサンドボックスではありません。測定時には対象の restore、clean、build に加え、
> Analyzer／Source Generator を含むコンパイラー呼び出しを再実行します。対象コードは、
> YAAP と同じユーザー権限でファイル操作、プロセス起動、通信などの副作用を起こせます。
> 信頼できないリポジトリを実行しないでください。`--isolated` は `bin`／`obj` の出力先を
> 分ける機能であり、セキュリティ境界ではありません。詳細は
> [セキュリティ方針](SECURITY.md#測定対象との信頼境界)を参照してください。

## クイックスタート

リポジトリから CLI を実行する例です。

```powershell
dotnet run --project src/Yaap.Cli --framework net10.0 -- profile path/to/App.slnx
dotnet run --project src/Yaap.Cli --framework net10.0 -- history list
```

Windows では GUI も起動できます。

```powershell
dotnet run --project src/Yaap.Gui --framework net10.0-windows
```

分離出力は既定で有効です。.NET の `--artifacts-path` を restore、clean、build へ渡しますが、
カスタムMSBuild targetなどによる任意の書き込みまでは防止しません。対象の標準 `bin`／`obj` を使う
場合だけ `--no-isolated` を指定します。YAAP自身にテレメトリや更新確認はありませんが、対象の
restore、build、Analyzer、Source Generatorは、対象の構成や実装に従って通信する可能性があります。

## 配布物

ローカルで自己完結型バイナリを作成する例です。

```powershell
./eng/build.ps1 publish --runtime win-x64 --framework net10.0
```

```sh
./eng/build.sh publish --runtime linux-x64 --framework net10.0
```

出力は `artifacts/publish/<RID>/<TFM>/` の `cli`、Windowsでは `gui` に作成されます。
`Yaap.BuildLogger.dll` は測定に必要なため、実行ファイルと同じディレクトリに保持してください。
正式なリリース用アーカイブには、利用対象の実行ファイルに加えて `LICENSE`、
`THIRD-PARTY-NOTICES.txt`、README、CHANGELOGを含めます。

配布アーカイブを展開した後は、SDKプロジェクトを開かずに実行できます。WindowsのCLIとGUIは
次のように起動します。

```powershell
.\yaap.exe version
.\yaap.exe profile C:\path\to\App.slnx
.\yaap-gui.exe
```

Linux／macOSのCLIは、展開先で実行権限を確認して起動します。

```sh
chmod +x ./yaap
./yaap version
./yaap profile /path/to/App.slnx
```

ソースは .NET 8／10 を対象にします。公開する自己完結型バイナリのOS、CPUアーキテクチャ、TFMは
各リリースの添付ファイルと [CHANGELOG](CHANGELOG.md) で確認してください。

## CLI と GUI

CLI と GUI は同じ Core を使用し、測定条件、履歴フィルター、既存binlog解析、比較、全件exportを
両方で利用できます。標準出力とファイル選択、テーマなど表示媒体に応じた操作差は、
[機能対応表](docs/index.md#cliとguiの機能対応表)に明記しています。

## データとプライバシー

履歴には測定値のほか、対象の絶対パス、SDK／OS情報、Git情報、診断、binlogが含まれ得ます。
binlogやビルド出力には、対象プロジェクト由来の機密情報が含まれる可能性があります。
履歴はローカルに保存され、YAAP自身は送信しません。共有前に内容を確認し、不要な履歴は削除してください。

## ドキュメント

最初に [ドキュメントガイド](docs/index.md) を参照してください。

- [利用ガイド](docs/usage.md) — CLI／GUI、履歴、比較
- [測定設計](docs/measurement.md) — 値の意味と制約
- [トラブルシュート](docs/troubleshooting.md) — エラーコードと対処
- [設計](docs/architecture.md) — コンポーネントとデータフロー
- [開発ガイド](docs/development.md)／[テスト方針](docs/testing.md)
- [DeepReviewガイド](docs/deep-review.md) — 明示起動専用の最高水準リポジトリ総合レビュー
- [変更履歴](CHANGELOG.md)、[貢献ガイド](CONTRIBUTING.md)、
  [セキュリティ方針](SECURITY.md)、[サポート方針](SUPPORT.md)

## ライセンス

YAAP は [MIT License](LICENSE) で提供します。配布に含まれる第三者ソフトウェアは
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) を参照してください。
