# Furniture Overlap

Tailsideの家具を重ねて配置し、表示順を調整できるModのソースコードです。
配布・導入・操作説明はSteamワークショップに掲載予定です。公開後、このREADMEから案内します。

## ビルド

.NET SDK・PowerShell・Tailside本体・BepInEx 5（Unity Mono向け）が必要です。
リポジトリのルートで、ゲームの場所を指定して実行します。

```powershell
.\Build.ps1 -GameRoot 'D:\SteamLibrary\steamapps\common\Tailside'
```

成果物は `release/` に出力されます。ゲームやBepInExのDLLは同梱しません。

`tests/` は開発用です。実行には通常のセーブから隔離したゲームコピーと、別途用意する検証用データが必要です。

## ライセンス

Mod独自のコード・文章は [0BSD](LICENSE) です。無保証で提供します。
ゲーム画像と第三者ライブラリは対象外で、それぞれの権利・ライセンスに従います。
