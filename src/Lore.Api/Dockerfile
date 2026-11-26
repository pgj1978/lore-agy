FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Lore.slnx ./
COPY src/Lore.Core/Lore.Core.csproj src/Lore.Core/
COPY src/Lore.Api/Lore.Api.csproj src/Lore.Api/
RUN dotnet restore src/Lore.Api/Lore.Api.csproj
COPY src/Lore.Core/ src/Lore.Core/
COPY src/Lore.Api/ src/Lore.Api/
RUN dotnet publish src/Lore.Api/Lore.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Lore.Api.dll"]
