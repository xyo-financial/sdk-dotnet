.PHONY: all build test coverage pack clean format generate docker-build docker-test

all: build test

build:
	dotnet build Xyo.Sdk.sln -c Release

test:
	dotnet test Xyo.Sdk.sln -c Release --verbosity normal

coverage:
	dotnet test Xyo.Sdk.sln -c Release --collect:"XPlat Code Coverage"

format:
	dotnet format Xyo.Sdk.sln

format-check:
	dotnet format Xyo.Sdk.sln --verify-no-changes

pack:
	dotnet pack src/Xyo.Sdk/Xyo.Sdk.csproj -c Release -o ./packages

clean:
	dotnet clean Xyo.Sdk.sln
	rm -rf packages/ TestResults/

generate:
	npx -y @openapitools/openapi-generator-cli generate \
		-i ../specs/openapi.yml \
		-g csharp \
		-o ./src/Xyo.Generated \
		--additional-properties=packageName=Xyo.Generated,targetFramework=net8.0,nullableReferenceTypes=true,netCoreProjectFile=true,library=generichost \
		--global-property apiTests=false,modelTests=false,apiDocs=false,modelDocs=false

docker-build:
	docker build -t xyo-sdk-dotnet:latest .

docker-test:
	docker build --target test -t xyo-sdk-dotnet:test .
