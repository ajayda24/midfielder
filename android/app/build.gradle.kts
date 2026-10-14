plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "dev.midfielder.android"
    compileSdk = 35

    defaultConfig {
        applicationId = "dev.midfielder"
        // Android 10: TLS 1.3 in the platform, and the version whose clipboard limits we design around.
        minSdk = 29
        targetSdk = 35
        // Each CI build gets a higher versionCode, so a newer APK installs as an update over an older one.
        val build = System.getenv("GITHUB_RUN_NUMBER")?.toIntOrNull() ?: 1
        versionCode = build
        versionName = "0.1.$build"
    }

    // One fixed key for every build (debug and release). Android only installs an update over an existing app
    // when the signatures match; a per-machine debug key changes on every CI run and makes updates fail with
    // "App not installed". This key is for personal sideloading: anyone with the repo can sign with it, so use a
    // private key (e.g. from CI secrets) before distributing builds to others.
    signingConfigs {
        create("sideload") {
            storeFile = file("midfielder-sideload.jks")
            storePassword = "midfielder-sideload"
            keyAlias = "midfielder"
            keyPassword = "midfielder-sideload"
        }
    }

    buildTypes {
        debug {
            signingConfig = signingConfigs.getByName("sideload")
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.getByName("sideload")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    lint {
        warningsAsErrors = false
        abortOnError = true
        checkReleaseBuilds = true
    }

    packaging {
        resources.excludes += setOf("META-INF/versions/9/OSGI-INF/MANIFEST.MF", "META-INF/DEPENDENCIES")
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    implementation(project(":core"))
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.zxing.core)
    debugImplementation(libs.leakcanary)
}
