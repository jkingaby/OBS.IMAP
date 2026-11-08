FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends dovecot-imapd openssl ca-certificates python3 && rm -rf /var/lib/apt/lists/* \
    && groupadd -g 2000 fixturemail && useradd -u 2000 -g fixturemail -d /srv/mail fixturemail
WORKDIR /work
COPY . .
RUN dotnet restore OBS-IT.IMAP.slnx --locked-mode
COPY docker/integration/dovecot.conf /etc/dovecot/dovecot.conf
RUN sed -i 's/\r$//' docker/integration/run.sh && chmod +x docker/integration/run.sh
ENV OBS_IMAP_INTEGRATION=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
ENTRYPOINT ["/work/docker/integration/run.sh"]
