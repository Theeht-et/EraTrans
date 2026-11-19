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

## Technology

- C# / WPF
- .NET 10
- OpenAI .NET SDK
- GitHub Actions for build and test
