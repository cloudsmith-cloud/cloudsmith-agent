FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY nuget.config .
COPY src/CloudSmith.Runner/CloudSmith.Runner.csproj src/CloudSmith.Runner/
RUN dotnet restore src/CloudSmith.Runner/CloudSmith.Runner.csproj
COPY . .
RUN dotnet publish src/CloudSmith.Runner/CloudSmith.Runner.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
VOLUME ["/etc/cloudsmith/certs"]
ENV CLOUDSMITH_RUNNER__CERTTHUMPRINT=""
ENV CLOUDSMITH_RUNNER__APIBASEURL=""
ENV CLOUDSMITH_RUNNER__RUNNERNAME=""
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CloudSmith.Runner.dll"]
