import {dotnet} from './dotnet.js';

const bootConfig = await (await fetch('./dotnet.boot.json')).json();

const {runMain} = await dotnet
    .withDiagnosticTracing(false)
    .withConfig(bootConfig)
    .create();

await runMain();
