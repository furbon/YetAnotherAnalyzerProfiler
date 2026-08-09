# GitHub 初期設定・運用ガイド

この文書は、YAAPを初めてGitHubへpushするときの設定と、v0.1.0をNuGet／GitHub Releasesへ
公開するまでの手順をまとめたmaintainer向けrunbookです。チェック項目は上から順に実施します。

`<OWNER>`はGitHubの個人またはOrganization名、`<REPOSITORY>`はrepository名に置き換えます。
推奨repository名は `YetAnotherAnalyzerProfiler`、恒久的な公開default branchは `main`、開発branchは
現在の `develop/v0.1.0` です。初回だけ、GitHub Actionsを含む開発branchを一時defaultにして安全に
`main`をbootstrapします。実在しない値をソースへ仮登録しないでください。

## 用意済みのGitHub Actions

| Workflow | 起動条件 | 内容 |
| --- | --- | --- |
| `CI` | `main`、`develop/**`、`agent/**`へのpush、Pull Request、手動実行 | Linux／Windows／macOS、.NET 8／10、WPF起動、format、repository guard、pack、自己完結型配布物を検証 |
| `Release` | `vX.Y.Z` tagのpush、または`main`からの確認付き手動実行 | version／公開文書を検証し、NuGet packageと4 RIDのarchiveを作成・実行確認・attestした後、NuGetとGitHub Releaseを公開 |
| Dependabot | 毎週 | NuGetとGitHub Actionsの更新Pull Requestを`develop/v0.1.0`へ作成 |

Hosted runnerに複数の.NET SDKがpreinstallされていてもlaneが混ざらないよう、setup jobは
`RUNNER_TEMP`配下のjob専用ディレクトリへ指定SDKだけをinstallします。

CIを任意のタイミングで実行するには、default branchへworkflowが入った後に
**Actions → CI → Run workflow** を開き、検証するbranchを選びます。入力項目はありません。
push／Pull Request時と同じfull matrixが走ります。

ReleaseをGitHub上から実行する手順は[公開操作](#公開操作)に限定します。Release jobは
`release` Environmentの承認前にはpublish権限やnuget.orgユーザー名へアクセスできません。

## 1. 初回push前

- [ ] GitHubの所有者を決める。複数maintainerへ引き継ぐ可能性がある場合は、個人accountより
      Organization所有を優先する。
- [ ] repository名、公開可否、説明、連絡窓口を決める。本ガイドはpublic OSSを前提とする。
- [ ] GitHubで **New repository** を開き、`<OWNER>/<REPOSITORY>`を空で作成する。
      **Add a README**、`.gitignore`、licenseの自動追加はすべて無効にし、既存履歴と競合させない。
- [ ] GitHub accountとnuget.org accountで2要素認証とrecovery方法を確認する。
- [ ] `git log --all --stat`、commit author、tracked filesを確認し、credential、個人用path、binlog、
      history、build output、ローカル試験結果が過去履歴にもないことを確認する。秘密が見つかった場合は、
      push前に失効させたうえで履歴を書き換え、全commitを再監査する。
- [ ] `LICENSE`、`THIRD-PARTY-NOTICES.txt`、`README.md`、`SECURITY.md`、`SUPPORT.md`、
      `CODE_OF_CONDUCT.md`、`CONTRIBUTING.md`の公開内容と非公開連絡先の運用可否を確認する。
- [ ] `./eng/install-git-hooks.ps1` を実行する。
- [ ] Windowsで `./eng/build.ps1 verify` と `dotnet format YetAnotherAnalyzerProfiler.slnx
      --verify-no-changes` を通し、`git status --short`が空であることを確認する。
- [ ] GitHubが表示するURLを使い、remoteを登録する。SSHを使う場合はURLだけを置き換える。

```powershell
git remote add origin https://github.com/<OWNER>/<REPOSITORY>.git
git remote -v
git remote get-url origin
```

- [ ] 誤ったURLを登録した場合は、追加し直さず次で修正する。

```powershell
git remote set-url origin https://github.com/<OWNER>/<REPOSITORY>.git
```

- [ ] 最初にActionsを含む `develop/v0.1.0` をpushし、その後に履歴上の `main` をpushする。空の
      repositoryでは通常、最初のbranchがdefaultになる。tagと `agent/**` branchはまだpushしない。

```powershell
git push -u origin develop/v0.1.0
git push -u origin main
```

初回から `main` を先にdefaultにすると、現在の `main` にはworkflowがないため `workflow_dispatch`と
初回Pull Requestのcheckを起動できません。`develop/v0.1.0`を一時defaultにし、v0.1.0のrelease candidateを
CI付きPull Requestで `main`へ統合してからdefaultを切り替えます。

## 2. Repository基本設定

初回push後、**Settings → General** を設定します。

- [ ] 初回bootstrap中の **Default branch**が `develop/v0.1.0` であることを確認する。v0.1.0を
      `main`へ統合した直後に、恒久defaultの `main`へ切り替える。
- [ ] Descriptionを設定する。例:
      `Roslyn Analyzer／Source Generatorをコンパイラー報告値から測定するオフライン対応プロファイラー`
- [ ] Topicsへ `dotnet`、`roslyn`、`analyzer`、`source-generator`、`profiler`、`wpf`を登録する。
- [ ] Websiteは公式ページができるまで空欄にし、仮URLを登録しない。
- [ ] **Issues**を有効にする。Projects、Discussionsは実際に運用するときだけ有効にし、Wikiは文書の
      二重管理を避けるため無効にする。
- [ ] Pull Request mergeは履歴方針に合わせて **Allow merge commits**だけを有効にする。
      **Automatically delete head branches**は有効にする。
- [ ] repository名変更やtransferを行う場合は、local remote、nuget.org Trusted Publishing policy、
      package metadata、attestation確認コマンドを同時に更新する。

### Actions

**Settings → Actions → General**を次のように設定します。

- [ ] Actionsを有効にする。許可元を限定できる場合はrepository内workflow、`actions/*`、
      `NuGet/login@*`だけを許可する。
- [ ] **Require actions to be pinned to a full-length commit SHA**を有効にする。workflow内の外部Actionは
      すべてfull SHAで固定され、Dependabotが更新する。
- [ ] **Workflow permissions**は **Read repository contents and packages permissions**にする。
      **Allow GitHub Actions to create and approve pull requests**は無効のままにする。Release jobが必要な
      `contents: write`等はworkflow内でjob単位に限定済みである。
- [ ] public forkからのworkflowは、初期運用では **Require approval for all external contributors**を
      推奨する。承認前にdiff、とくにworkflowとbuild scriptの変更を確認する。
- [ ] Artifact and log retentionは30日を目安にする。CI／Release artifact自体はworkflowで7日に限定される。

GitHubは、`GITHUB_TOKEN`のdefault権限をread-onlyにし、各workflowで最小権限だけを付与すること、
外部Actionをfull commit SHAへ固定することを推奨しています。
[Actions設定](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/enabling-features-for-your-repository/managing-github-actions-settings-for-a-repository)と
[安全な利用](https://docs.github.com/en/actions/reference/security/secure-use)も確認してください。

## 3. Ruleset

設定直後はrequired check名が候補に出ないため、まず `develop/v0.1.0` の初回CIを最後まで実行します。
その後、**Settings → Rules → Rulesets**で次を作ります。従来のbranch protectionと重複させません。

### `main` ruleset

- [ ] Enforcement statusを **Active**、targetを明示的なbranch `main`にする。一時defaultの指定は使わない。
- [ ] branchの削除とforce pushを禁止する。
- [ ] Pull Requestを必須にし、conversation resolutionを必須にする。
- [ ] merge methodは **Merge**に限定する。
- [ ] maintainerが1人だけならrequired approvalは0にし、2人以上になった時点で1以上、stale approvalの
      dismiss、latest reviewable pushの別maintainer承認を有効にする。1人運用で1 approvalを要求すると
      自分のPull Requestを自分で承認できず停止する。
- [ ] 次のrequired status checksをGitHub Actions app由来として登録し、branchを最新にすることを要求する。

```text
Verify Linux / net8.0
Verify Linux / net10.0
Verify Windows / net8.0
Verify Windows / net10.0
Verify macOS / net10.0
Package / linux-x64
Package / win-x64
Package / osx-x64
Package / osx-arm64
```

### `develop/**` ruleset

- [ ] targetを `develop/**` にし、削除とforce pushだけを禁止する。
- [ ] このrepositoryのagent workflowは、検証済みの `agent/**` をlocalで `--no-ff` mergeしてから
      `develop/**`へpushするため、現時点ではPull Requestやrequired status checkを強制しない。
      外部contributorの変更はPull Requestで受け、maintainerが検証後に統合する。

### `v*.*.*` tag ruleset

- [ ] target tagを `v*.*.*` にし、更新と削除を禁止する。
- [ ] 手動Release workflowがtagを作成するため、GitHub Actionsがbypassできることを検証しないまま
      tag作成自体を制限しない。作成者制限を使う場合はGitHub Actions appを明示的なbypass actorにする。
- [ ] Rulesetのbypass権限は最小限にし、緊急時も理由を記録する。

RulesetでPull Request、status check、署名、force push等を制御できます。利用中のGitHub planで使える
項目は[Rulesetのルール一覧](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets)を確認します。

## 4. Securityと依存関係

**Settings → Security / Code security and analysis**で次を有効にします。public repositoryでは利用可能な
機能が多い一方、private repositoryではplanにより制約があります。

- [ ] Dependency graph
- [ ] Dependabot alerts
- [ ] Dependabot security updates
- [ ] Secret scanning
- [ ] Push protection
- [ ] Private vulnerability reporting
- [ ] Code scanningの **CodeQL default setup**（languageはC#）

Private vulnerability reportingを有効にしたら、`SECURITY.md`の案内から実際に非公開報告を開始できるか
確認します。行動規範違反の窓口はsecurity報告と分離し、公開Issueを使わせないでください。

GitHubの[Security設定](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/enabling-features-for-your-repository/managing-security-and-analysis-settings-for-your-repository)では、push protectionが対応するsecretのpushを事前に阻止し、code scanningが脆弱性やerrorを検出します。

## 5. Release EnvironmentとNuGet

### GitHub Environment

**Settings → Environments → New environment**で名前が完全一致する `release` を作成します。

- [ ] Deployment branches and tagsをselected refsにし、`main` branchと `v*.*.*` tagだけを許可する。
- [ ] maintainerが2人以上ならrequired reviewerを設定し、**Prevent self-review**を有効にする。
      1人運用では自分で開始したReleaseを承認できなくなるため、required reviewerを自分に設定する場合も
      Prevent self-reviewは有効にしない。
- [ ] 管理者によるprotection ruleのbypassを無効にできる場合は無効にする。
- [ ] Environment secret `NUGET_USER`へnuget.orgのprofile usernameを登録する。email addressやAPI keyを
      入れない。Repository secretに `NUGET_API_KEY` は作成しない。

Environmentの承認が終わるまでsecretはjobへ渡りません。詳細はGitHubの
[Environmentとdeployment protection](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments)を参照してください。

### nuget.org Trusted Publishing

nuget.orgへsign inし、accountまたはOrganizationの **Trusted Publishing** でGitHub policyを作成します。

- [ ] Package `YetAnotherAnalyzerProfiler.Tool` の所有者を決め、可能なら複数maintainerをownerへ追加する。
- [ ] package IDが未使用であり、類似名や商標上の問題がないことをnuget.orgで最終確認する。
- [ ] policy ownerをpackage所有者と同じaccount／Organizationにする。
- [ ] Repository owner: `<OWNER>`
- [ ] Repository: `<REPOSITORY>`
- [ ] Workflow file: `release.yml`（`.github/workflows/`は付けない）
- [ ] Environment: `release`
- [ ] private repositoryでpolicyが一時有効になった場合は、表示された期間内に初回publishを行って
      repository ID／owner IDを固定する。

Release workflowは、Environment承認後にGitHub OIDC tokenをnuget.orgの1時間有効な短期API keyへ
交換し、検証済みpackageだけをpushします。長期API keyの保管とrotationは不要です。設定形式と制約は
[nuget.org Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)を参照してください。

## 6. 初回push後

- [ ] `develop/v0.1.0`の **Actions → CI** が全job成功したことを確認する。
- [ ] 上記check名が表示された後に `main` rulesetへrequired checksを登録する。
- [ ] Issue formを1件ずつpreviewし、blank Issueが無効で、日本語の項目が正しく表示されることを確認する。
- [ ] public profileからREADME、license、security policy、support、contribution guideが認識されることを確認する。
- [ ] `SECURITY.md`のprivate vulnerability reportingと、`CODE_OF_CONDUCT.md`の非公開窓口を別accountから試す。
- [ ] Dependabotの初回実行、dependency graph、CodeQL default setup、secret scanningの状態を確認する。
- [ ] Social preview画像、repository labels、milestoneは実際の運用に必要なものだけ設定する。
- [ ] `CODEOWNERS`は実在するteam／accountとreview体制が決まった後に追加する。仮ownerは登録しない。

## 7. v0.1.0公開前

- [ ] `develop/v0.1.0`の内容を凍結し、`eng/Version.props`が唯一のversion sourceで `0.1.0`であることを確認する。
- [ ] `CHANGELOG.md`のv0.1.0に公開日、material capability、互換性、security/privacy、既知の制約を記載し、
      `.github/release-notes/v0.1.0.md`と整合させる。
- [ ] `README.md`の「まだ公開されていません」を公開用install案内へ変更する。
- [ ] `SECURITY.md`の公開済みversionとsupport期間を更新する。
- [ ] `docs/release-checklist.md`を全項目確認する。
- [ ] Repository URL、package project URL、source commit metadataが `<OWNER>/<REPOSITORY>`と公開候補commitへ
      解決されることをActions artifact内のnuspecで確認する。
- [ ] `YetAnotherAnalyzerProfiler.Tool`のID、owner、Trusted Publishing policy、`release` Environmentの
      `NUGET_USER`を再確認する。
- [ ] `develop/v0.1.0`で手動 `CI`を実行し、全jobを成功させる。
- [ ] Release candidateを `develop/v0.1.0`から `main`へのPull Requestとしてreviewし、required checksを
      全て通してmerge commitで統合する。
- [ ] `main`のcommitが意図したrelease candidateと一致し、worktreeとGitHubに未公開の `v0.1.0` tagが
      存在しないことを確認する。
- [ ] **Settings → General → Default branch**を `develop/v0.1.0`から `main`へ切り替える。以後は
      `main`を恒久defaultとし、一時設定へ戻さない。
- [ ] `main`で手動 `CI`を実行し、全jobをもう一度成功させる。

## 8. 公開操作

推奨経路はGitHub UIからの手動Releaseです。

1. [ ] **Actions → Release → Run workflow**を開く。
2. [ ] Branchに `main` を選ぶ。他branchを選ぶとworkflowはfail-closedする。
3. [ ] `release_tag`へ `v0.1.0` を入力する。
4. [ ] `confirm_publish`を有効にして実行する。
5. [ ] `gate`、全OS／TFM検証、4 RIDのarchive検証とproducer attestationが成功したことを確認する。
6. [ ] `release` Environmentの待機jobで、commit、version、workflow diff、release note、artifact名を確認して承認する。
7. [ ] workflowが検証済みcommitへtagを作成または照合し、NuGetのdigest確認後にGitHub Releaseを公開するまで待つ。

既存運用との互換性のため、maintainerが `v0.1.0` tagを `main`の検証済みcommitへpushしても同じ
Release workflowが動きます。二つの経路を同時に開始しないでください。release単位のconcurrencyにより
publishは直列化され、進行中runは自動cancelされません。

Release workflowが作成したtagは `GITHUB_TOKEN`によるため、同じtag push workflowを再帰起動しません。
GitHubは `GITHUB_TOKEN`が発生させたeventを原則として新しいworkflow runへ連鎖させない仕様です。
[workflowのtrigger](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow)を参照してください。

## 9. 公開後

- [ ] [NuGet Gallery](https://www.nuget.org/)で `YetAnotherAnalyzerProfiler.Tool` v0.1.0、owner、README、license、
      project URL、repository commit、dependenciesを確認する。
- [ ] 一時ディレクトリまたはclean machineで `dotnet tool install --global
      YetAnotherAnalyzerProfiler.Tool --version 0.1.0`、`yaap version`、help、最小profileを確認する。
- [ ] GitHub Releaseの本文、4 archive、NuGet package、`SHA256SUMS.txt`、tagとcommitを確認する。
- [ ] 各archiveを対応OS／architectureで展開し、README記載のCLI起動を再確認する。WindowsはGUIのSTA起動、
      light／dark表示も確認する。
- [ ] GitHub CLIでartifact attestationを検証する。

```powershell
gh attestation verify yaap-win-x64.zip --repo <OWNER>/<REPOSITORY>
gh attestation verify YetAnotherAnalyzerProfiler.Tool.0.1.0.nupkg --repo <OWNER>/<REPOSITORY>
```

- [ ] publicな `v0.1.0` ReleaseとNuGet packageのURLをREADME、announcement、support案内で使用し、
      Actions artifactの一時URLを配布しない。
- [ ] Release workflowとCodeQL／Dependabotに失敗やalertが残っていないことを確認する。
- [ ] `develop/v0.1.0`を閉じる手順と次versionの `develop/v...`、`eng/Version.props`、Dependabotの
      target branch、release note雛形を同じ変更で準備する。
- [ ] tag、NuGet package、GitHub Releaseを削除して「やり直す」運用はしない。公開後の修正は新しい
      SemVerで行い、必要なら影響版をunlistしてsecurity／incident記録を残す。

## 失敗時の判断

- tag作成前の失敗: 原因を修正して `main`へreview済み変更をmergeし、新しいcommitで最初から実行する。
- tag作成後・NuGet公開前の失敗: tagが意図したcommitを指すことを確認し、同じworkflowをrerunする。
- NuGet公開後・GitHub Release公開前の失敗: versionを再利用して異なるbyteを作らない。同じtag commitから
  byte-for-byte同じpackageを再現し、workflowのdigest guardを通してReleaseを復旧する。
- NuGet上の同versionが異なるdigestの場合: workflowは停止する。公開を続けず、account compromiseや
  誤公開を調査し、必要に応じてunlistと新しいversionでの修正を行う。
- public GitHub Releaseが既にある場合: workflowは変更を拒否する。assetの差し替えではなく新しいversionを使う。

より詳細なartifact復旧条件は[公開チェックリスト](release-checklist.md#再実行と復旧)を参照してください。
