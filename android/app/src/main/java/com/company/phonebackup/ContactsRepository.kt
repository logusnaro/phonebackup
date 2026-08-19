package com.company.phonebackup

import android.content.ContentValues
import android.content.Context
import android.provider.ContactsContract
import kotlinx.serialization.Serializable
import java.nio.charset.StandardCharsets
import java.time.Instant
import java.util.UUID

@Serializable data class ContactDto(val id: String, val displayName: String, val phoneNumbers: List<String> = emptyList(), val emails: List<String> = emptyList(), val company: String? = null, val notes: String? = null, val updatedAt: String = "")

class ContactsRepository(private val context: Context) {
    fun readAll(): List<ContactDto> {
        data class Basic(val id: String, val name: String)
        val basics = mutableListOf<Basic>()
        val phonesByContact = mutableMapOf<String, MutableList<String>>()
        val emailsByContact = mutableMapOf<String, MutableList<String>>()
        val projection = arrayOf(ContactsContract.Contacts._ID, ContactsContract.Contacts.DISPLAY_NAME)
        context.contentResolver.query(ContactsContract.Contacts.CONTENT_URI, projection, null, null, ContactsContract.Contacts.DISPLAY_NAME + " COLLATE LOCALIZED")?.use { cursor ->
            while (cursor.moveToNext()) {
                basics.add(Basic(cursor.getString(0), cursor.getString(1) ?: ""))
            }
        }
        context.contentResolver.query(ContactsContract.CommonDataKinds.Phone.CONTENT_URI,
            arrayOf(ContactsContract.CommonDataKinds.Phone.CONTACT_ID, ContactsContract.CommonDataKinds.Phone.NUMBER), null, null, null)?.use { cursor ->
            while (cursor.moveToNext()) phonesByContact.getOrPut(cursor.getString(0)) { mutableListOf() }.add(cursor.getString(1))
        }
        context.contentResolver.query(ContactsContract.CommonDataKinds.Email.CONTENT_URI,
            arrayOf(ContactsContract.CommonDataKinds.Email.CONTACT_ID, ContactsContract.CommonDataKinds.Email.ADDRESS), null, null, null)?.use { cursor ->
            while (cursor.moveToNext()) emailsByContact.getOrPut(cursor.getString(0)) { mutableListOf() }.add(cursor.getString(1))
        }
        val updatedAt = Instant.now().toString()
        return basics.map {
            val stableId = UUID.nameUUIDFromBytes("android-contact:${it.id}".toByteArray(StandardCharsets.UTF_8)).toString()
            ContactDto(stableId, it.name, phonesByContact[it.id].orEmpty().distinct(), emailsByContact[it.id].orEmpty().distinct(), updatedAt = updatedAt)
        }
    }

    fun upsertManaged(contact: ContactDto) {
        val resolver = context.contentResolver
        val existing = resolver.query(ContactsContract.RawContacts.CONTENT_URI, arrayOf(ContactsContract.RawContacts._ID), "${ContactsContract.RawContacts.ACCOUNT_TYPE}=? AND ${ContactsContract.RawContacts.SOURCE_ID}=?", arrayOf("com.company.phonebackup", contact.id), null)?.use { if (it.moveToFirst()) it.getString(0) else null }
        val raw = ContentValues().apply { put(ContactsContract.RawContacts.ACCOUNT_NAME, "업무 통합 주소록"); put(ContactsContract.RawContacts.ACCOUNT_TYPE, "com.company.phonebackup"); put(ContactsContract.RawContacts.SOURCE_ID, contact.id) }
        val rawUri = if (existing == null) resolver.insert(ContactsContract.RawContacts.CONTENT_URI, raw) ?: return else android.content.ContentUris.withAppendedId(ContactsContract.RawContacts.CONTENT_URI, existing.toLong())
        if (existing != null) resolver.delete(ContactsContract.Data.CONTENT_URI, "${ContactsContract.Data.RAW_CONTACT_ID}=?", arrayOf(existing))
        val rawId = rawUri.lastPathSegment ?: return
        resolver.insert(ContactsContract.Data.CONTENT_URI, ContentValues().apply { put(ContactsContract.Data.RAW_CONTACT_ID, rawId); put(ContactsContract.Data.MIMETYPE, ContactsContract.CommonDataKinds.StructuredName.CONTENT_ITEM_TYPE); put(ContactsContract.CommonDataKinds.StructuredName.DISPLAY_NAME, contact.displayName) })
        contact.phoneNumbers.distinct().forEach { resolver.insert(ContactsContract.Data.CONTENT_URI, ContentValues().apply { put(ContactsContract.Data.RAW_CONTACT_ID, rawId); put(ContactsContract.Data.MIMETYPE, ContactsContract.CommonDataKinds.Phone.CONTENT_ITEM_TYPE); put(ContactsContract.CommonDataKinds.Phone.NUMBER, it); put(ContactsContract.CommonDataKinds.Phone.TYPE, ContactsContract.CommonDataKinds.Phone.TYPE_MOBILE) }) }
        contact.emails.distinct().forEach { resolver.insert(ContactsContract.Data.CONTENT_URI, ContentValues().apply { put(ContactsContract.Data.RAW_CONTACT_ID, rawId); put(ContactsContract.Data.MIMETYPE, ContactsContract.CommonDataKinds.Email.CONTENT_ITEM_TYPE); put(ContactsContract.CommonDataKinds.Email.ADDRESS, it); put(ContactsContract.CommonDataKinds.Email.TYPE, ContactsContract.CommonDataKinds.Email.TYPE_WORK) }) }
    }
}
