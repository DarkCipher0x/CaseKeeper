# Third-party notices

CaseKeeper is built with these components. Each one keeps its own licence.

| Component | Licence | Use |
|---|---|---|
| [.NET 10 runtime](https://github.com/dotnet/runtime) and WPF | MIT | Shipped with the app (self-contained) |
| [DocumentFormat.OpenXml](https://github.com/dotnet/Open-XML-SDK) 3.5.1 | MIT | Word (.docx) export and filling Word templates |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) 10.0.14 | MIT | Database access |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) bundle_e_sqlcipher 2.1.11 | Apache-2.0 | Native SQLite with SQLCipher encryption |
| [SQLCipher](https://github.com/sqlcipher/sqlcipher) (community edition, bundled by SQLitePCLRaw) | BSD-style (Zetetic LLC) | Encrypting the database |
| [PDFsharp](https://github.com/empira/PDFsharp) (PDFsharp-WPF) 6.2.4 | MIT | PDF output: bundles, letters and reports |
| [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) 1.4.2 | MIT | Backups and archives |
| Windows DPAPI (System.Security.Cryptography.ProtectedData, part of the .NET runtime) | MIT | Protecting the stored AI API key |

Optional, not bundled: [Ollama](https://ollama.com) (MIT) for the local AI features, if you install it yourself.

SQLitePCLRaw is licensed under the Apache License, Version 2.0 (https://www.apache.org/licenses/LICENSE-2.0).
SQLCipher: Copyright (c) 2008-2024 Zetetic LLC. All rights reserved. Redistribution and use in source and binary forms,
with or without modification, are permitted under the terms of its BSD-style licence
(https://www.zetetic.net/sqlcipher/license/).
