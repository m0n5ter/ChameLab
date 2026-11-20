# A headless ChameLab sync node (chamelab sync serve) for a Raspberry Pi, a NAS or a VPS.
# Build from software/chamelab:
#   docker build -f ChameLab.Cli/deploy/sync-node.Dockerfile -t chamelab-sync-node .
# Join the group of a device showing Settings → Device sync → Add device:
#   docker run -it --rm -v chamelab-sync:/data chamelab-sync-node --join DE1-ABCD-1234 --name "Home NAS"
# then keep it running:
#   docker run -d --restart unless-stopped --network host -v chamelab-sync:/data chamelab-sync-node
# --network host lets devices on the same network find it over mDNS; without it only the relay is used.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + \
 && rid=linux-x64 && [ "$TARGETARCH" = "arm64" ] && rid=linux-arm64 || true \
 && dotnet publish ChameLab.Cli -c Release -r "$rid" --self-contained -o /out

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
COPY --from=build /out /app
VOLUME /data
ENTRYPOINT ["/app/chamelab", "sync", "serve", "--data", "/data"]
