FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Копируем csproj и восстанавливаем зависимости
COPY src/TeaMate.Core/TeaMate.Core.csproj src/TeaMate.Core/
COPY src/TeaMate.Mcp.EmployeeReader/TeaMate.Mcp.EmployeeReader.csproj src/TeaMate.Mcp.EmployeeReader/
RUN --mount=type=cache,id=teamate-nuget,target=/root/.nuget/packages dotnet restore src/TeaMate.Mcp.EmployeeReader/TeaMate.Mcp.EmployeeReader.csproj

# Копируем исходники и собираем релиз
COPY src/TeaMate.Core/ src/TeaMate.Core/
COPY src/TeaMate.Mcp.EmployeeReader/ src/TeaMate.Mcp.EmployeeReader/
RUN --mount=type=cache,id=teamate-nuget,target=/root/.nuget/packages dotnet publish src/TeaMate.Mcp.EmployeeReader/TeaMate.Mcp.EmployeeReader.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /out .
EXPOSE 8081
ENTRYPOINT ["dotnet", "TeaMate.Mcp.EmployeeReader.dll"]

