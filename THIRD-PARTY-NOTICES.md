# Third-Party Notices

UniToDoの独自部分は、ルートの`LICENSE`に記載したMIT Licenseで提供します。著作権者はuniuni ([https://x.com/lept_on](https://x.com/lept_on))です。

`src/TaskManager.App/Assets/TaskManager.ico`と`src/TaskManager.App/wwwroot/favicon.svg`は本プロジェクトで独自に作成した素材であり、外部素材ではありません。これらも本プロジェクトのMIT Licenseの対象です。

以下の第三者コンポーネントには、本プロジェクトのMIT Licenseではなく、それぞれのライセンスが適用されます。共通のライセンス本文は`licenses/`に収録します。頒布用の自己完結ランタイムには、発行に使用した.NET SDK付属の.NET Library LicenseとThirdPartyNoticesに加え、実行時パッケージ付属のLICENSE、NOTICE、Third-Party Notices全文を`licenses/packages/`へ収録します。収録元とSHA-256は`licenses/package-legal-files.json`で確認できます。

| コンポーネント | バージョン | 用途 | ライセンス・著作権表示 | 確認先 |
|---|---:|---|---|---|
| Google.Apis.Calendar.v3 | 1.75.0.4206 | 実行時 | Apache-2.0 / Copyright 2026 Google LLC | [NuGet](https://www.nuget.org/packages/Google.Apis.Calendar.v3/1.75.0.4206) |
| Google.Apis | 1.75.0 | 実行時 | Apache-2.0 / Copyright 2021 Google LLC | [NuGet](https://www.nuget.org/packages/Google.Apis/1.75.0) |
| Google.Apis.Auth | 1.75.0 | 実行時 | Apache-2.0 / Copyright 2021 Google LLC | [NuGet](https://www.nuget.org/packages/Google.Apis.Auth/1.75.0) |
| Google.Apis.Core | 1.75.0 | 実行時 | Apache-2.0 / Copyright 2021 Google LLC | [NuGet](https://www.nuget.org/packages/Google.Apis.Core/1.75.0) |
| Microsoft.Data.Sqlite.Core | 10.0.11 | 実行時 | MIT / Microsoft Corporation、.NET Foundation and Contributors | [NuGet](https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/10.0.11) |
| Newtonsoft.Json | 13.0.4 | 実行時 | MIT / Copyright (c) 2007 James Newton-King | [NuGet](https://www.nuget.org/packages/Newtonsoft.Json/13.0.4) |
| SQLitePCLRaw.core | 2.1.12 | 実行時 | Apache-2.0 / Copyright 2014-2024 SourceGear, LLC | [NuGet](https://www.nuget.org/packages/SQLitePCLRaw.core/2.1.12) |
| SQLitePCLRaw.provider.winsqlite3 | 2.1.11 | 実行時 | Apache-2.0 / Copyright 2014-2024 SourceGear, LLC | [NuGet](https://www.nuget.org/packages/SQLitePCLRaw.provider.winsqlite3/2.1.11) |
| System.Management | 7.0.2 | 実行時 | MIT / Microsoft Corporation、.NET Foundation and Contributors | [NuGet](https://www.nuget.org/packages/System.Management/7.0.2) |
| System.CodeDom | 7.0.0 | ソースのビルド・テスト | MIT / Microsoft Corporation、.NET Foundation and Contributors | [NuGet](https://www.nuget.org/packages/System.CodeDom/7.0.0) |
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.11 | 自己完結ランタイム | Microsoft .NET Library Licenseと付属ThirdPartyNotices | [.NETライセンス情報](https://github.com/dotnet/core/blob/main/license-information.md) |
| Microsoft.AspNetCore.App.Runtime.win-x64 | 10.0.11 | 自己完結ランタイム | Microsoft .NET Library Licenseと付属ThirdPartyNotices | [.NETライセンス情報](https://github.com/dotnet/core/blob/main/license-information.md) |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 | 10.0.11 | 自己完結ランタイム | Microsoft .NET Library Licenseと付属ThirdPartyNotices | [.NETライセンス情報](https://github.com/dotnet/core/blob/main/license-information.md) |

Google APIクライアント、SQLitePCLRaw、Microsoft.Data.Sqlite、Newtonsoft.Json、System.Management、System.CodeDomのバージョンとライセンスは、NuGetパッケージのメタデータを基準にしています。依存関係の対応表は`licenses/dependencies.json`、発行時に収集したパッケージ付属文書の一覧は`licenses/package-legal-files.json`です。

Codex CLI、Ollama、TypeWhisper、Google CalendarサービスおよびWindows標準コンポーネントは、このプロジェクトから再頒布しません。利用者が別途取得するこれらの製品・サービスには、各提供元の条件が適用されます。TypeWhisper連携ソースはMIT対象ですが、ビルド時に外部配置の`TypeWhisper.PluginSDK.dll`を参照し、SDK自体は頒布物に含めません。
