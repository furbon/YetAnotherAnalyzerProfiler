# 開発ガイド

## 必要なもの

- .NET 8 SDK または .NET 10 SDK
- WPF／GUIのビルドと実行には Windows
- Git（製品の実行自体には必須ではありません）

`global.json` は .NET 8 以上の最新メジャーを選択します。Core と CLI は `net8.0;net10.0`、
GUI は `net8.0-windows;net10.0-windows` を対象にします。バージョンは
`eng/Version.props` だけを変更し、アセンブリ／ファイル版の4番目は常に0です。

## 共通コマンド

```powershell
./eng/build.ps1 verify
./eng/build.ps1 publish --runtime win-x64 --framework net10.0
```

```sh
./eng/build.sh verify
./eng/build.sh publish --runtime linux-x64 --framework net10.0
```

`verify` はリポジトリガード、locked restore、format、警告をエラーにした build、Core／CLI／
GUIテスト、実 Analyzer／Generator 統合テスト、ローカルNuGet feed／lock file試験を実行します。
GUIテストは Windows だけで実行されます。

新しい NuGet パッケージは中央管理し、提供元、保守状況、利用実績、ライセンス、脆弱性、
推移依存、対象フレームワーク、代替案を作業計画に記録してください。製品依存は最小限にします。

## ファイルと品質

ソーステキストは UTF-8 BOM、CRLF、スペースインデントです。実行可能な `.sh` だけは
UTF-8（BOMなし）とLFです。`AGENTS.md`、`.github/copilot-instructions.md`、
`eng/agent-instructions.md` は完全一致させます。共通ハーネスがこれらとローカルパス、秘密情報、
成果物混入、版番号重複を検査します。
