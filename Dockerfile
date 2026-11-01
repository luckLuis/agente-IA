# Imagen para desarrollo. Use autenticación SQL (la integrada de Windows no funciona en Linux).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/SqlGraphCopilot.Core/SqlGraphCopilot.Core.csproj src/SqlGraphCopilot.Core/
COPY src/SqlGraphCopilot.Api/SqlGraphCopilot.Api.csproj src/SqlGraphCopilot.Api/
RUN dotnet restore src/SqlGraphCopilot.Api/SqlGraphCopilot.Api.csproj
COPY src/ src/
RUN dotnet publish src/SqlGraphCopilot.Api/SqlGraphCopilot.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
# Cadena de conexión por variable de entorno, p. ej.:
#   -e SqlGraphCopilot__Instances__0__ConnectionString="Server=...;User ID=sqlgraph_reader;Password=...;TrustServerCertificate=True"
ENTRYPOINT ["dotnet", "SqlGraphCopilot.Api.dll"]
