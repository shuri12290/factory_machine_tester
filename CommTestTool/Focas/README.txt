FANUC FOCAS2 DLL 配置フォルダ
==============================

このフォルダに以下のファイルを配置してください：

  Fwlib64.dll  （64bit環境用・現在の設定）

配置後にビルドすると、自動的に bin フォルダにコピーされます。

32bit環境（Fwlib32.dll）を使用する場合：
  1. Fwlib32.dll をこのフォルダに配置
  2. CommTestTool.csproj の <Include="Focas\Fwlib64.dll"> を
     <Include="Focas\Fwlib32.dll"> に変更
  3. Adapters.cs の FOCAS_DLL 定数を "Fwlib32.dll" に変更
  4. プロジェクトのビルド設定を x86 に変更

入手先：
  - FANUC FOCAS2 Library CD（A02B-0207-K737）
  - または信頼できるGitHubリポジトリ
    例: github.com/strangesast/fwlib
