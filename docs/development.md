# 開発ガイド

## 必要なもの

- .NET SDK 8.0.423 と 10.0.302（検証では両方を使用）
- WPF／GUIのビルドと実行には Windows
- Git（製品の実行自体には必須ではありません）

`global.json` と `eng/toolchain.json` は検証SDKを固定します。Core と CLI は `net8.0;net10.0`、
GUI は `net8.0-windows;net10.0-windows` を対象にします。バージョンは
`eng/Version.props` だけを変更し、アセンブリ／ファイル版の4番目は常に0です。

## 共通コマンド

```powershell
./eng/build.ps1 verify
./eng/build.ps1 pack --framework net10.0
./eng/build.ps1 publish --runtime win-x64 --framework net10.0
```

```sh
./eng/build.sh verify
./eng/build.sh pack --framework net10.0
./eng/build.sh publish --runtime linux-x64 --framework net10.0
```

`verify` はリポジトリガード、locked restore、format、警告をエラーにした build、Core／CLI／
GUIテスト、実 Analyzer／Generator 統合テスト、ローカルNuGet feed／lock file試験を実行します。
GUIテストは Windows だけでDebug、両対象フレームワークに対して実行され、STA起動スモークを含みます。
`pack` はCLIを `YetAnotherAnalyzerProfiler.Tool` という.NETツールにし、パッケージ内容を許可リストで
検査した後、一時tool-pathへインストールして `version` と `help` を実行します。`net10.0` の `verify`
にもこの検証が含まれます。
`publish` はRID専用の一時lock fileで復元した後にlocked publishを行い、成果物の許可リスト、同梱する
ライセンス／通知、CLIの起動を検証します。Windows向けGUIは実際にウィンドウを開いて終了まで確認します。

新しい NuGet パッケージは中央管理し、提供元、保守状況、利用実績、ライセンス、脆弱性、
推移依存、対象フレームワーク、代替案を作業計画に記録してください。製品依存は最小限にします。
production lock fileに現れる全パッケージと版は `THIRD-PARTY-NOTICES.txt` に記載し、共通ガードで
同期を検証します。

## GitHubリリース

Pull RequestではGitHub Actionsの全OS／両TFM検証が完了しない限り、branch protectionでマージを許可しない
運用を前提にします。同じブランチやPRへ追加pushした場合は古い実行をキャンセルし、各jobにはタイムアウトを
設定しています。Hosted runnerに複数SDKがpreinstallされていても `global.json` のroll-forwardでlaneが
混ざらないよう、各setup jobは `RUNNER_TEMP` 配下へ対象SDKだけを分離installします。.NET 8 laneは
SDK間のNuGet lock graph表現差がtracked lock fileを変更しないよう、`obj/sdk8.packages.lock.json`へ
framework別restoreを分離し、.NET 10 laneでtracked lockとVisual Studio rebuildの再現性を検証します。

正式リリースは、まず `eng/Version.props` を更新して通常のPR検証を完了します。推奨経路では
GitHub Actionsから `main` の手動Releaseを確認付きで開始し、全検証後にworkflowが同じ版の
`vX.Y.Z` tagを検証済みcommitへ作成します。既存のtag pushも同じ公開経路を使用します。
`.github/workflows/release.yml` はtagと唯一の版情報が一致することを全jobで確認し、
.NET 8／10、Windows／Linux／macOSを再検証してからNuGetツールと4 RIDの自己完結型アーカイブを作成します。
公開jobはGitHub Environment `release` に置き、Environmentへnuget.org profile名の`NUGET_USER` secretと
必要な承認者を設定します。NuGet publishにはrepository、workflow、Environmentへ制限したTrusted Publishingを
使い、GitHub OIDC tokenから短期credentialを取得します。長期API keyは保存しません。
成果物の検証が完了するまでNuGet pushとGitHub Release公開は行われません。workflowは公開済みReleaseの
変更を拒否し、既存NuGetと候補のSHA-256を公開前後に照合します。パッケージと各RIDアーカイブは生成jobで
provenance attestationを作り、draftへ同一成果物を添付してから公開します。復旧とホスト設定の必須条件は
[公開チェックリスト](release-checklist.md)と[GitHub初期設定・運用ガイド](github-setup.md)を参照してください。

GitLab CIはLinuxの両TFMと、`windows`／`macos`タグを持つ自己管理runnerでプラットフォーム検証を行います。
runner管理者は隔離された一時作業領域、必要SDK、PowerShell、WPFの対話可能セッションを用意してください。
現在の公開経路の正本はGitHub release workflowです。GitLabから公開する場合は、保護tag、承認environment、
digest照合、producer provenanceが同等になる別設計を承認してから有効化します。

## ファイルと品質

ソーステキストは UTF-8 BOM、CRLF、スペースインデントです。実行可能な `.sh` と
`.agents/skills/` 配下は UTF-8（BOMなし）とLF、NuGet が管理する `packages.lock.json` は
UTF-8（BOMなし）とCRLFです。`AGENTS.md`、`.github/copilot-instructions.md`、
`eng/agent-instructions.md` は完全一致させます。共通ハーネスがこれらとローカルパス、秘密情報、
成果物混入、版番号重複を検査します。
