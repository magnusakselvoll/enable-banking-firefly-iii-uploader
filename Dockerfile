FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY EnableBankingUploader.slnx .
COPY src/EnableBankingUploader.Core/EnableBankingUploader.Core.csproj src/EnableBankingUploader.Core/
COPY src/EnableBankingUploader.Cli/EnableBankingUploader.Cli.csproj src/EnableBankingUploader.Cli/
RUN dotnet restore src/EnableBankingUploader.Cli/EnableBankingUploader.Cli.csproj

COPY src/ src/
RUN dotnet publish src/EnableBankingUploader.Cli -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "EnableBankingUploader.Cli.dll"]
