FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only source inputs into the build context; bin/obj and local NuGet
# artifacts are excluded by .dockerignore to prevent stale restore metadata.
COPY . .
RUN dotnet restore src/Majazi.Api/Majazi.Api.csproj
RUN dotnet publish src/Majazi.Api/Majazi.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends libkrb5-3 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Majazi.Api.dll"]
