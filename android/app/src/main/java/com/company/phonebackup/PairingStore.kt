package com.company.phonebackup

import android.content.Context
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

data class PairingConfig(val deviceId: String, val token: String, val serverUrl: String, val certificateSha256: String)

class PairingStore(context: Context) {
    private val prefs = EncryptedSharedPreferences.create(context, "pairing", MasterKey.Builder(context).setKeyScheme(MasterKey.KeyScheme.AES256_GCM).build(), EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV, EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM)
    fun load(): PairingConfig? = prefs.getString("deviceId", null)?.let { PairingConfig(it, prefs.getString("token", "")!!, prefs.getString("serverUrl", "")!!, prefs.getString("certificate", "")!!) }
    fun save(config: PairingConfig) { prefs.edit().putString("deviceId", config.deviceId).putString("token", config.token).putString("serverUrl", config.serverUrl).putString("certificate", config.certificateSha256).apply() }
    fun clear() { prefs.edit().clear().apply() }
}
