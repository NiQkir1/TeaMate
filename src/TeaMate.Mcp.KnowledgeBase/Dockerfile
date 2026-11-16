FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

COPY src/TeaMate.Core/TeaMate.Core.csproj src/TeaMate.Core/
COPY src/TeaMate.Mcp.KnowledgeBase/TeaMate.Mcp.KnowledgeBase.csproj src/TeaMate.Mcp.KnowledgeBase/
RUN --mount=type=cache,id=teamate-nuget,target=/root/.nuget/packages dotnet restore src/TeaMate.Mcp.KnowledgeBase/TeaMate.Mcp.KnowledgeBase.csproj

COPY src/TeaMate.Core/ src/TeaMate.Core/
COPY src/TeaMate.Mcp.KnowledgeBase/ src/TeaMate.Mcp.KnowledgeBase/
RUN --mount=type=cache,id=teamate-nuget,target=/root/.nuget/packages dotnet publish src/TeaMate.Mcp.KnowledgeBase/TeaMate.Mcp.KnowledgeBase.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /out .
EXPOSE 8086
ENTRYPOINT ["dotnet", "TeaMate.Mcp.KnowledgeBase.dll"]

