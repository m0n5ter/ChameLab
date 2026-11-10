# For hosts whose own glibc or systemd is too old for the binary (deploy.sh --docker): the
# chiseled image brings the Ubuntu 24.04 libraries the AOT build links against.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
COPY ChameLab.Relay /app/ChameLab.Relay
ENV ASPNETCORE_URLS=https://+:7443 \
    Kestrel__Certificates__Default__Path=/certs/relay.crt \
    Kestrel__Certificates__Default__KeyPath=/certs/relay.key
EXPOSE 7443
ENTRYPOINT ["/app/ChameLab.Relay"]
