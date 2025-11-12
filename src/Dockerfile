FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-env

WORKDIR /app
 
# Copy the .csproj files and restore dependencies
COPY Dock8s/Dock8s.API/*.csproj ./Dock8s/Dock8s.API/
COPY Dock8s/Dock8s.Application/*.csproj ./Dock8s/Dock8s.Application/
COPY Dock8s/Dock8s.Infrastructure/*.csproj ./Dock8s/Dock8s.Infrastructure/

# Restore the dependencies
RUN dotnet restore ./Dock8s/Dock8s.API/Dock8s.API.csproj
 
# Copy the rest of the application code
COPY Dock8s/ ./Dock8s/
 
# Build the project
WORKDIR /app/Dock8s/Dock8s.API/

RUN dotnet publish -c Release -o out
 
FROM mcr.microsoft.com/dotnet/aspnet:9.0

WORKDIR /app/

COPY --from=build-env /app/Dock8s/Dock8s.API/out .
 
# Run and expose the application.
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80

ENTRYPOINT ["dotnet", "Dock8s.API.dll"]