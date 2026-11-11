FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

COPY src/Fotoprint.Core/Fotoprint.Core.csproj src/Fotoprint.Core/
COPY src/Fotoprint.Mcp.Calendar/Fotoprint.Mcp.Calendar.csproj src/Fotoprint.Mcp.Calendar/
RUN dotnet restore src/Fotoprint.Mcp.Calendar/Fotoprint.Mcp.Calendar.csproj

COPY src/Fotoprint.Core/ src/Fotoprint.Core/
COPY src/Fotoprint.Mcp.Calendar/ src/Fotoprint.Mcp.Calendar/
RUN dotnet publish src/Fotoprint.Mcp.Calendar/Fotoprint.Mcp.Calendar.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /out .
EXPOSE 8084
ENTRYPOINT ["dotnet", "Fotoprint.Mcp.Calendar.dll"]

