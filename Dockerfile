# syntax=docker/dockerfile:1

# =============================================================================
#  Beyond Movement API
# =============================================================================
#  Build from the REPOSITORY ROOT, not from src/BeyondMovement.Api:
#
#     docker build -t beyond-movement-api .
#
#  The API project references four sibling projects, so the build context has to
#  span the whole src/ tree.
# =============================================================================


# --- build ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /source

# The .csproj files are copied first, on their own, so that `restore` lands in a
# layer that only changes when a dependency changes. Editing C# then rebuilds
# without re-downloading every NuGet package.
COPY Directory.Build.props ./
COPY src/BeyondMovement.Api/BeyondMovement.Api.csproj                         src/BeyondMovement.Api/
COPY src/BeyondMovement.Infrastructure/BeyondMovement.Infrastructure.csproj   src/BeyondMovement.Infrastructure/
COPY src/BeyondMovement.Modules.Athletes/BeyondMovement.Modules.Athletes.csproj src/BeyondMovement.Modules.Athletes/
COPY src/BeyondMovement.Modules.Identity/BeyondMovement.Modules.Identity.csproj src/BeyondMovement.Modules.Identity/
COPY src/BeyondMovement.SharedKernel/BeyondMovement.SharedKernel.csproj       src/BeyondMovement.SharedKernel/

RUN dotnet restore src/BeyondMovement.Api/BeyondMovement.Api.csproj

# Now the source. Only src/ - tests are not part of a runtime image, and running
# them belongs in CI where a failure blocks the merge.
COPY src/ src/

RUN dotnet publish src/BeyondMovement.Api/BeyondMovement.Api.csproj \
    --configuration $BUILD_CONFIGURATION \
    --no-restore \
    --output /app


# --- runtime ----------------------------------------------------------------
# aspnet, not sdk: no compiler, no NuGet cache, no build tooling in the shipped
# image. Smaller, and a smaller attack surface.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# The .NET images define APP_UID as a pre-created non-root user. Running as root
# in a container is a habit worth not having.
USER $APP_UID

# Kestrel listens here. Terminate TLS at the load balancer and let the container
# speak plain HTTP - ASPNETCORE_URLS can override this if the platform insists
# on a different port (some inject their own PORT variable).
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# Everything else - the database connection string, the JWT signing key, the
# Postmark token - is injected at run time. See .env.example for the full list.
# Nothing secret is baked into this image.
ENV ASPNETCORE_ENVIRONMENT=Production

COPY --from=build /app .

ENTRYPOINT ["dotnet", "BeyondMovement.Api.dll"]

# No HEALTHCHECK instruction on purpose: the aspnet image ships no curl or wget,
# and orchestrators (Kubernetes, ECS, App Service) run their own probes rather
# than reading this one. Point those probes at:
#     /health         includes a database check - use for readiness
#     /api/v1/ping    no dependencies       - use for liveness
