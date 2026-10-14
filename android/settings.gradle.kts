pluginManagement {
    repositories {
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
        mavenCentral()
    }
}

rootProject.name = "midfielder"

// :core is plain JVM Kotlin (protocol, crypto, transport, sync logic) and builds anywhere.
include(":core")

// :app needs the Android SDK. Include it only when an SDK is configured, so `./gradlew :core:test`
// works on machines (and CI jobs) without one.
val localProps = file("local.properties")
val hasSdk = System.getenv("ANDROID_HOME") != null ||
    System.getenv("ANDROID_SDK_ROOT") != null ||
    (localProps.exists() && localProps.readText().contains("sdk.dir"))
if (hasSdk) {
    include(":app")
}
