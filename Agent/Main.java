package com.lengjiao;

import android.net.LocalServerSocket;
import android.net.LocalSocket;
import java.io.InputStream;
import java.io.OutputStream;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import android.content.ClipData;
import android.os.IBinder;

public class Main {
    private static OutputStream outStream;

    public static void main(String[] args) {
        System.out.println("[Agent] Starting LengJiao Clipboard Agent v2...");
        try {
            // 1. Get ServiceManager.getService("clipboard")
            Class<?> smClass = Class.forName("android.os.ServiceManager");
            Method getService = smClass.getMethod("getService", String.class);
            IBinder binder = (IBinder) getService.invoke(null, "clipboard");

            // 2. Get IClipboard.Stub.asInterface(binder)
            Class<?> stubClass = Class.forName("android.content.IClipboard$Stub");
            Method asInterface = stubClass.getMethod("asInterface", IBinder.class);
            Object iClipboard = asInterface.invoke(null, binder);

            // 3. Start PC listener
            new Thread(() -> {
                try {
                    LocalServerSocket server = new LocalServerSocket("lengjiao_agent");
                    System.out.println("[Agent] Listening on local abstract socket: lengjiao_agent");
                    while (true) {
                        LocalSocket client = server.accept();
                        System.out.println("[Agent] PC Connected!");
                        outStream = client.getOutputStream();
                        InputStream in = client.getInputStream();

                        byte[] buffer = new byte[65536];
                        int len;
                        while ((len = in.read(buffer)) != -1) {
                            String msg = new String(buffer, 0, len, StandardCharsets.UTF_8).trim();
                            if (msg.startsWith("SET_CLIP:")) {
                                String text = msg.substring(9);
                                setClipboard(iClipboard, text);
                                System.out.println("[Agent] Set PC clipboard to phone.");
                            }
                        }
                    }
                } catch (Exception e) {
                    e.printStackTrace();
                }
            }).start();

            // 4. Poll clipboard changes from phone to PC
            String lastClip = "";
            while (true) {
                String currentClip = getClipboard(iClipboard);
                if (currentClip != null && !currentClip.equals(lastClip)) {
                    lastClip = currentClip;
                    sendToPc("CLIP_EVENT:" + currentClip);
                }
                Thread.sleep(800); // 800ms polling is fast enough
            }

        } catch (Exception e) {
            e.printStackTrace();
        }
    }

    private static void setClipboard(Object iClipboard, String text) {
        try {
            ClipData clip = ClipData.newPlainText("LengJiao", text);
            Method setPrimaryClip = null;
            try {
                setPrimaryClip = iClipboard.getClass().getMethod("setPrimaryClip", ClipData.class, String.class, int.class);
                setPrimaryClip.invoke(iClipboard, clip, "com.android.shell", 0);
            } catch (NoSuchMethodException e) {
                setPrimaryClip = iClipboard.getClass().getMethod("setPrimaryClip", ClipData.class, String.class);
                setPrimaryClip.invoke(iClipboard, clip, "com.android.shell");
            }
        } catch (Exception e) {
            e.printStackTrace();
        }
    }

    private static String getClipboard(Object iClipboard) {
        try {
            Method getPrimaryClip = null;
            ClipData cd = null;
            try {
                getPrimaryClip = iClipboard.getClass().getMethod("getPrimaryClip", String.class, int.class);
                cd = (ClipData) getPrimaryClip.invoke(iClipboard, "com.android.shell", 0);
            } catch (NoSuchMethodException e) {
                getPrimaryClip = iClipboard.getClass().getMethod("getPrimaryClip", String.class);
                cd = (ClipData) getPrimaryClip.invoke(iClipboard, "com.android.shell");
            }
            if (cd != null && cd.getItemCount() > 0) {
                CharSequence seq = cd.getItemAt(0).getText();
                if (seq != null) return seq.toString();
            }
        } catch (Exception e) {
        }
        return null;
    }

    private static void sendToPc(String message) {
        if (outStream != null) {
            try {
                outStream.write((message + "\0").getBytes(StandardCharsets.UTF_8));
                outStream.flush();
            } catch (Exception e) {
            }
        }
    }
}
