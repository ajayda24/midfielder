// The Android Gradle Plugin is put on the root classpath only when an Android SDK is configured, so that
// `./gradlew :core:test` works on machines without the SDK (or without access to Google's Maven repo).
// It has to live at the root so the Kotlin Android plugin (same classloader) can see it.
buildscript {
    val localProps = file("local.properties")
    val hasSdk = !System.getenv("ANDROID_HOME").isNullOrBlank() ||
        !System.getenv("ANDROID_SDK_ROOT").isNullOrBlank() ||
        (localProps.exists() && localProps.readText().contains("sdk.dir"))
    if (hasSdk) {
        repositories {
            google()
            mavenCentral()
        }
        dependencies {
            classpath("com.android.tools.build:gradle:${libs.versions.agp.get()}")
        }
    }
}

plugins {
    alias(libs.plugins.kotlin.jvm) apply false
    alias(libs.plugins.kotlin.android) apply false
    alias(libs.plugins.wire) apply false
}
