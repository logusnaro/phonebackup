package com.company.phonebackup

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.Worker
import androidx.work.WorkerParameters
import androidx.work.WorkManager
import java.util.concurrent.TimeUnit

class ReminderWorker(context: Context, params: WorkerParameters) : Worker(context, params) {
    override fun doWork(): Result {
        val channelId = "weekly_backup_review"
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val manager = applicationContext.getSystemService(android.app.NotificationManager::class.java)
            manager.createNotificationChannel(android.app.NotificationChannel(channelId, "주간 백업 알림", android.app.NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "Smart Switch 백업과 90일 삭제 검토 알림"
            })
        }
        val intent = Intent(applicationContext, MainActivity::class.java)
        val flags = PendingIntent.FLAG_UPDATE_CURRENT or
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) PendingIntent.FLAG_IMMUTABLE else 0
        val pending = PendingIntent.getActivity(applicationContext, 401, intent, flags)
        val notification = NotificationCompat.Builder(applicationContext, channelId)
            .setSmallIcon(R.drawable.ic_phonebackup)
            .setContentTitle("PB 주간 백업 검토")
            .setContentText("Smart Switch 백업을 확인하고 90일 지난 통화녹음 삭제 후보를 검토하세요.")
            .setContentIntent(pending)
            .setAutoCancel(true)
            .setPriority(NotificationCompat.PRIORITY_DEFAULT)
            .build()
        runCatching { NotificationManagerCompat.from(applicationContext).notify(401, notification) }
        return Result.success()
    }

    companion object {
        private const val UNIQUE_NAME = "weekly-backup-review"

        fun schedule(context: Context) {
            val request = PeriodicWorkRequestBuilder<ReminderWorker>(7, TimeUnit.DAYS)
                .setInitialDelay(7, TimeUnit.DAYS)
                .build()
            WorkManager.getInstance(context).enqueueUniquePeriodicWork(
                UNIQUE_NAME,
                ExistingPeriodicWorkPolicy.KEEP,
                request
            )
        }
    }
}
