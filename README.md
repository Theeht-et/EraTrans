# EraTrans

A lightweight Windows desktop tool for translating ERA games.

## Features

- Extract and edit JSON dictionaries while preserving legacy ERA keys and unknown fields.
- Translate selected or pending dictionary entries through OpenAI-compatible Chat Completions APIs.
- Configure multiple providers with a custom Base URL, model name, and API key.
- Encrypt saved API keys with Windows DPAPI for the current Windows user.
- Keep AI output in the editor for review; dictionary files are only changed after the user saves.
- Apply translations to a separate game working copy. The original game directory is not modified.

## AI provider configuration

Configure a provider in the dictionary editor. The Base URL should point to the API's OpenAI-compatible base endpoint, usually ending in /v1, rather than a full /chat/completions path. The client uses the official OpenAI .NET SDK, which supports custom endpoints.

Provider configuration is stored under the current user's AppData directory. API keys are protected with Windows DPAPI (CurrentUser); the key is not written to the settings file as plaintext. DPAPI-protected keys are tied to the Windows user account that saved them.

The initial integration supports the OpenAI-compatible Chat Completions protocol. It does not yet implement separate native Claude, Gemini, or Responses API protocols.

AI translation only targets editable, non-variable entries whose stage is 0, whose original text is non-empty, and whose translation is empty. AI results are mapped by dictionary entry index and are not automatically saved or marked as reviewed (stage remains unchanged). Existing translations and ignored entries are never overwritten.

## Download a Windows build

Every successful GitHub Actions CI run publishes a self-contained Windows x64 ZIP package named EraTrans-win-x64.

1. Open the repository's Actions tab.
2. Open the successful CI run for the commit or pull request you want.
3. Download EraTrans-win-x64 from the Artifacts section.
4. Extract the ZIP archive and run EraTrans.App.exe.

The package includes the .NET runtime, so a separate .NET installation is not required. Keep the extracted files together. CI artifacts are retained for 30 days.

## Technology

- C# / WPF
- .NET 10
- OpenAI .NET SDK
- GitHub Actions for build, test, and Windows packaging
