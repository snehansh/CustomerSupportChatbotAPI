# ============================================================
# Stage 1: BUILD
# Use the official .NET 10 SDK image to compile and publish
# the application. This stage is only used during the build
# process and will NOT be included in the final image, which
# keeps the deployed image small and lean.
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Set the working directory inside the container for this stage.
# All subsequent commands in this stage run from /app.
WORKDIR /app

# Copy the project file first and restore NuGet packages.
# We do this as a separate step before copying the rest of the code
# so that Docker can cache the restored packages layer. This means
# that if only your .cs files change (not your dependencies), Docker
# won't re-download all NuGet packages on the next build - saving time.
COPY CustomerSupportChatbotAPI.csproj ./
RUN dotnet restore

# Copy the rest of the source code into the container.
COPY . ./

# Publish the application in Release mode to the /app/publish folder.
# --no-restore skips a redundant package restore since we already did it above.
RUN dotnet publish CustomerSupportChatbotAPI.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish


# ============================================================
# Stage 2: RUNTIME
# Use the slim ASP.NET Core 10 runtime image (no SDK tools).
# This is the final image that actually gets deployed to Render.
# It is much smaller (~200 MB vs ~800 MB for the SDK image),
# which means faster deployments and a smaller attack surface.
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Set the working directory for the runtime container.
WORKDIR /app

# Copy only the published output from the build stage into this image.
# Nothing from the SDK stage (source code, build tools) is carried over.
COPY --from=build /app/publish .

# Tell ASP.NET Core to listen on port 8080.
# Render routes external traffic to whatever port the container exposes,
# and 8080 is the Render convention for web services.
ENV ASPNETCORE_URLS=http://+:8080

# Expose port 8080 so Render knows which port to forward traffic to.
EXPOSE 8080

# Define the entry point - the command that runs when the container starts.
# "CustomerSupportChatbotAPI" must match your project/assembly name exactly.
ENTRYPOINT ["dotnet", "CustomerSupportChatbotAPI.dll"]
