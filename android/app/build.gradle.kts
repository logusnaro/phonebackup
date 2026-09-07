plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android { namespace = "com.company.phonebackup"; compileSdk = 35
    // Galaxy S7 (SM-G930S) can still be on its original Android 6.0 build, so support API 23+.
    defaultConfig { applicationId = "com.company.phonebackup"; minSdk = 23; targetSdk = 35; versionCode = 15; versionName = "2.0.1" }
    signingConfigs {
        getByName("debug") {
            storeFile = rootProject.file("../.android/debug.keystore")
            storePassword = "android"
            keyAlias = "androiddebugkey"
            keyPassword = "android"
        }
        create("release") {
            val keystorePath = providers.environmentVariable("PB_KEYSTORE_PATH").orNull
                ?: rootProject.file("../.android/phonebackup-release.keystore").absolutePath
            val keystorePassword = providers.environmentVariable("PB_KEYSTORE_PASSWORD").orNull
            val keyAliasValue = providers.environmentVariable("PB_KEY_ALIAS").orNull ?: "phonebackup"
            val keyPasswordValue = providers.environmentVariable("PB_KEY_PASSWORD").orNull
            if (!keystorePassword.isNullOrBlank() && !keyPasswordValue.isNullOrBlank()) {
                storeFile = file(keystorePath)
                storePassword = keystorePassword
                keyAlias = keyAliasValue
                keyPassword = keyPasswordValue
            }
        }
    }
    buildTypes { release { isMinifyEnabled = false; signingConfig = signingConfigs.getByName("release"); proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro") } }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures { compose = true; buildConfig = true }
    composeOptions { kotlinCompilerExtensionVersion = "1.5.14" }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.activity:activity-compose:1.9.2")
    implementation("androidx.compose.ui:ui:1.7.2")
    implementation("androidx.compose.material3:material3:1.3.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.6")
    implementation("androidx.work:work-runtime-ktx:2.9.1")
    implementation("androidx.documentfile:documentfile:1.0.1")
}
