FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/OBS.IMAP.Host/OBS.IMAP.Host.csproj --locked-mode
RUN dotnet publish src/OBS.IMAP.Host/OBS.IMAP.Host.csproj --no-restore --configuration Release --no-self-contained -o /app
RUN mkdir -p /app/notices/nuget && cd /root/.nuget/packages && find . -type f \( -iname '*license*' -o -iname '*notice*' -o -iname '*.nuspec' -o -iname 'readme*' -o -iname 'copying*' \) -exec cp --parents {} /app/notices/nuget \;

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled@sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c
WORKDIR /app
COPY --from=build /app .
COPY LICENSE NOTICE THIRD-PARTY-NOTICES.md /app/notices/
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "obs-imap.dll"]
CMD ["--help"]
