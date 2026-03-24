FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY EatHealthyCycle.csproj .
RUN dotnet restore EatHealthyCycle.csproj
COPY . .
RUN dotnet publish EatHealthyCycle.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Install Tesseract OCR + ImageMagick for image diet import
RUN apt-get update && apt-get install -y --no-install-recommends \
    tesseract-ocr \
    tesseract-ocr-spa \
    imagemagick \
    && rm -rf /var/lib/apt/lists/* \
    && sed -i 's/rights="none" pattern="PDF"/rights="read|write" pattern="PDF"/' /etc/ImageMagick-6/policy.xml 2>/dev/null || true

COPY --from=build /app .
EXPOSE 8080
ENV PORT=8080
ENTRYPOINT ["dotnet", "EatHealthyCycle.dll"]
