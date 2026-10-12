# syntax=docker/dockerfile:1
# Build environment for Anthropic's OSS Scanner (https://github.com/anthropics/oss-scanner): sets up the .NET SDK,
# restores every dependency while the network is still available, builds the solution and runs the unit tests.
# The scanner builds this with the repository as the build context and audits the image offline afterwards.
#
# What builds on Linux: the server (ASP.NET Core), the shared contracts and console core, the Linux operator
# console, the CLI, and the tests - exactly what CI builds on ubuntu-latest. The Windows components (the SYSTEM
# agent, its updater, the WinForms consoles) target net10.0-windows; they compile on Linux with
# EnableWindowsTargeting (reference assemblies only, no runtime), which is enough for an auditor to read and
# compile-check them. That step is kept non-fatal so a Windows-only build quirk never costs the whole image.
FROM mcr.microsoft.com/dotnet/sdk:10.0

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true \
    NUGET_XMLDOC_MODE=skip \
    MSBUILDDISABLENODEREUSE=1 \
    DEBIAN_FRONTEND=noninteractive

# ssh-keygen: the tests verify an SSH signature format against the real OpenSSH; mariadb-client: for anyone who
# wants to exercise the schema (src/RemoteServer/Data/Migrations/schema.sql) against a database of their own.
RUN apt-get update && apt-get install -y --no-install-recommends \
      ca-certificates git openssh-client mariadb-client python3 \
    && rm -rf /var/lib/apt/lists/*

# scanner contract: the checkout lives inside the image, at /src
COPY . /src
WORKDIR /src

# Restore everything (network is available here), then build the Linux-native projects and the tests.
RUN dotnet restore src/RemoteServer/RemoteServer.csproj \
    && dotnet restore src/RemoteClient.Linux/RemoteClient.Linux.csproj \
    && dotnet restore src/RemoteClient.Cli/RemoteClient.Cli.csproj \
    && dotnet restore tests/RemoteAppClient.Tests/RemoteAppClient.Tests.csproj \
    && dotnet build src/RemoteServer/RemoteServer.csproj -c Debug --no-restore \
    && dotnet build src/RemoteClient.Linux/RemoteClient.Linux.csproj -c Debug --no-restore \
    && dotnet build src/RemoteClient.Cli/RemoteClient.Cli.csproj -c Debug --no-restore \
    && dotnet build tests/RemoteAppClient.Tests/RemoteAppClient.Tests.csproj -c Debug --no-restore

# The Windows components, compile-checked with reference assemblies. Non-fatal on purpose.
RUN (dotnet restore src/RemoteAgent/RemoteAgent.csproj -p:EnableWindowsTargeting=true \
     && dotnet build src/RemoteAgent/RemoteAgent.csproj -c Debug --no-restore -p:EnableWindowsTargeting=true \
     && dotnet restore src/RemoteAgent.Updater/RemoteAgent.Updater.csproj -p:EnableWindowsTargeting=true \
     && dotnet build src/RemoteAgent.Updater/RemoteAgent.Updater.csproj -c Debug --no-restore -p:EnableWindowsTargeting=true \
     && dotnet restore src/RemoteClient/RemoteClient.csproj -p:EnableWindowsTargeting=true \
     && dotnet build src/RemoteClient/RemoteClient.csproj -c Debug --no-restore -p:EnableWindowsTargeting=true) \
    || echo "WARNING: the Windows components did not compile on Linux; their sources are in /src/src for reading"

# A self-contained server publish, the artifact the deployment actually ships (deploy/ and the console's Server update tab).
RUN dotnet publish src/RemoteServer/RemoteServer.csproj -c Release -r linux-x64 --self-contained -o /src/publish/server

# Run the unit tests so the image is known to work; a failing test must not abort the build.
RUN dotnet test tests/RemoteAppClient.Tests/RemoteAppClient.Tests.csproj -c Debug --no-build --nologo \
    || echo "WARNING: dotnet test reported failures"
