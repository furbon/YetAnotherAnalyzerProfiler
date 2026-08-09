# 公開チェックリスト

公開候補は、次の条件をすべて満たすまでNuGetやGitHub Releaseへ公開しません。

## ホスティング設定

- 公開default branchと、必須CIを要求するbranch protectionを設定する。
- `v*.*.*` tagの更新／削除を禁止し、release Environmentに承認者と`NUGET_USER`を設定する。
- nuget.org Trusted Publishing policyを公開元repository、`release.yml`、`release` Environmentへ
  制限し、長期`NUGET_API_KEY`をGitHubへ保存しない。
- GitHubのPrivate vulnerability reporting、または検証済みの同等な非公開窓口を有効にする。
- 行動規範の報告用に、セキュリティ報告とは分離した検証済みの非公開窓口を有効にし、
  `CODE_OF_CONDUCT.md`から到達できることを確認する。
- GitLabの自己管理runnerは専用・一時作業領域で動かし、保護変数を非保護pipelineへ渡さない。
- `RepositoryUrl`、package project URL、nuspecのcommitが公開元のrepositoryとtag commitに一致することをpack guardで確認する。
- 初回GitHub設定と段階別TODOは[GitHub初期設定・運用ガイド](github-setup.md)に従う。

## 成果物

1. `eng/Version.props`、tag、CHANGELOGの版と公開日を一致させ、同じ版の
   `.github/release-notes/v<version>.md`をCHANGELOGと同期する。
2. canonical verifyを全OS・両TFMで通す。GUIはSTA起動とライト／ダーク表示を確認する。
3. NuGet packを二回行い、byte-for-byteで同一であることを確認する。
4. 各アーカイブを対応CPUのrunnerで展開し、文書どおり`cli/yaap`を起動する。
5. packageと各アーカイブのproducer attestation、集約した`SHA256SUMS.txt`を作る。
6. version別Release Noteを本文に設定したdraftだけへ検証済み成果物を添付し、既存の公開Releaseは変更しない。

推奨する公開起動は、GitHubの **Actions → Release → Run workflow** で `main`、公開tag、
publish確認を指定する方法です。全検証後にEnvironment承認を行い、workflowが検証済みcommitのtagを
作成または照合します。既存のversion tag pushも同じ検証／公開経路を使用します。

利用者はGitHub CLIで由来を確認できます。`<owner>/<repository>`はNuGetメタデータの公開元へ置き換えます。

```sh
gh attestation verify yaap-linux-x64.zip --repo <owner>/<repository>
gh attestation verify YetAnotherAnalyzerProfiler.Tool.<version>.nupkg --repo <owner>/<repository>
```

`<owner>/<repository>`は公開元、`<version>`は検証するSemVer（例: `0.1.0`）へ置き換えます。

## 再実行と復旧

同じ版のNuGetが存在する場合は、公式feedから取得したbyteのSHA-256が候補と同じ場合だけ続行します。
push後も公式feedから再取得して照合し、不一致ならGitHub Releaseを公開しません。既存の公開Releaseは
workflowが拒否します。draftの添付失敗は同じworkflow runの検証済みartifactから再添付できます。

NuGetだけが公開され、GitHub Releaseが未公開の状態になった場合は版を作り直しません。同じtag commitと
同じdigestのartifactを使ってproducer attestationとdraftを復旧し、照合後に公開します。digestを再現できない
場合は公開を停止し、NuGetのunlist、インシデント記録、修正版の新しいSemVerを行います。
