# syntax=docker/dockerfile:1.4

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app
RUN mkdir /data && chown app:app /data
VOLUME /data
ENV ANCILLARY_STATE_PATH=/data/state.json
EXPOSE 6690
USER app

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY [".", "."]
RUN dotnet publish "src/HospitalSim.Ancillary/HospitalSim.Ancillary.csproj" \
    -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build --chown=app:app /app/publish .
ENTRYPOINT ["dotnet", "HospitalSim.Ancillary.dll"]
