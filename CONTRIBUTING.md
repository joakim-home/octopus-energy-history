# Contributing

Keep changes focused and describe the observable problem, expected result and verification. Discuss changes to tariff semantics or consumption storage before implementation.

Run targeted tests while developing, then the Release tests:

```sh
dotnet test tests/JoakimHomeDashboard.Tests -c Release
```

Use synthetic fixtures. Never commit credentials, database copies, account or meter identifiers, supplier responses, private hostnames, household screenshots or logs containing those details.

Pricing changes must preserve raw consumption, distinguish estimates from bills, and fail closed when supplier allocation is absent or inconsistent. Include boundary, retry and reconciliation coverage where relevant. Do not infer EV allocation from meter spikes or charging estimates.

AI-assisted contributions are welcome. Review generated code, disclose material AI assistance in the pull request, and provide reproducible test results. Do not send private supplier data or secrets to an AI service as part of a public contribution.

By contributing, you agree that your original contributions are provided under the repository's MIT license. Preserve third-party attribution and license notices.
