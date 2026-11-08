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
| [BouncyCastle.Cryptography](https://github.com/bcgit/bc-csharp) 2.7.0 | MIT | Checking the post-quantum (ML-DSA-65) signature on updates |
| [Anthropic](https://github.com/anthropics/anthropic-sdk-csharp) 13.6.0 | Apache-2.0 | Optional cloud AI: Claude through the Anthropic API |
| [Anthropic.Foundry](https://github.com/anthropics/anthropic-sdk-csharp) 1.0.0 | Apache-2.0 | Optional cloud AI: Claude on Microsoft Foundry |
| [OpenAI](https://github.com/openai/openai-dotnet) 2.15.0 | MIT | Optional cloud AI: OpenAI, and OpenAI models on Microsoft Foundry |
| [System.ClientModel](https://github.com/Azure/azure-sdk-for-net) 1.16.0, [Azure.Core](https://github.com/Azure/azure-sdk-for-net) 1.49.0, [Azure.Identity](https://github.com/Azure/azure-sdk-for-net) 1.17.0 | MIT | Used by the OpenAI and Foundry libraries (CaseKeeper uses key sign-in only) |
| [Microsoft.Identity.Client](https://github.com/AzureAD/microsoft-authentication-library-for-dotnet) 4.76.0 (and .Extensions.Msal), Microsoft.IdentityModel.Abstractions 6.35.0 | MIT | Dependencies of Azure.Identity |
| [Microsoft.Extensions.AI.Abstractions](https://github.com/dotnet/extensions) 10.5.1 and Microsoft.Extensions.* abstractions 10.0.10 | MIT | Dependencies of the AI libraries |
| Windows DPAPI (System.Security.Cryptography.ProtectedData, part of the .NET runtime) | MIT | Protecting the stored AI API keys and the optional GitHub update token |

Optional, not bundled: [Ollama](https://ollama.com) (MIT) for the local AI features, if you install it yourself.
The cloud AI services (Anthropic, OpenAI, Microsoft Foundry) are used under your own account and their terms.

SQLitePCLRaw, Anthropic and Anthropic.Foundry are licensed under the Apache License, Version 2.0 (https://www.apache.org/licenses/LICENSE-2.0).
SQLCipher: Copyright (c) 2008-2024 Zetetic LLC. All rights reserved. Redistribution and use in source and binary forms,
with or without modification, are permitted under the terms of its BSD-style licence
(https://www.zetetic.net/sqlcipher/license/).
