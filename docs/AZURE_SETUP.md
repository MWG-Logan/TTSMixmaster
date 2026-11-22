# Azure Blob Storage Setup Guide for TTSMixmaster

TTSMixmaster uploads your MP3s to Azure Blob Storage so Tabletop Simulator can stream them from
public URLs. You sign in with your Microsoft account and pick where the files go; there's no
key to copy.

## What you need

- An Azure subscription you can sign in to.
- A **Storage account** in it (or permission to use an existing one).

## Step 1: Create a storage account (once)

1. Go to the [Azure Portal](https://portal.azure.com) and create a **Storage account**:
   - choose a unique name (for example `ttsmixmaster2026`) and a region close to you;
   - **Standard** performance and **Locally-redundant storage (LRS)** keep it cheap.
2. Wait for the deployment to finish.

## Step 2: Sign in and pick the storage

1. In TTSMixmaster, open **Settings** and select **Sign in** under Azure. Windows shows its
   usual Microsoft account sign-in (or your browser opens).
2. Pick the **tenant**, **subscription**, **storage account** and **container** to upload to.
   You can create a new container from the picker.
3. TTSMixmaster checks what that container needs and tells you before it changes anything:
   - **Upload permission.** Uploading needs the **Storage Blob Data Contributor** role on the
     container (being the subscription Owner isn't enough by itself). If you're allowed to grant
     roles (Owner or User Access Administrator), the app can assign that role to you for this
     container only.
   - **Public read access.** Tabletop Simulator downloads the files anonymously, so the
     container must allow public *blob* access (files readable by URL; the container listing
     stays private). The app can switch this on for the container. Doing so also enables public
     access on the storage account, which can expose other containers in that account that were
     already set to allow anonymous access.
4. Save. Uploads now go to that container, and each file gets a public URL like:
   ```
   https://youraccount.blob.core.windows.net/<container>/<file>.mp3
   ```

TTSMixmaster remembers your sign-in and selection. A new role assignment can take a few minutes to
take effect; if an upload is denied right after granting access, wait and try again.

## Advanced: connection string

If you can't use Azure sign-in (for example, a storage account in a tenant you can't sign in
to), you can paste the storage account's connection string in the advanced Azure settings
instead. Find it in the portal under **Security + networking → Access keys**. It contains the
account key, which grants full access to the whole account, so treat it like a password.

## How it works

1. **Upload:** each audio file is uploaded to the container you picked.
2. **Public URLs:** the generated Tabletop Simulator objects point at those blob URLs.
3. **Tabletop Simulator:** players' clients stream the music directly from Azure.

## Cost estimation

Azure Blob Storage is very affordable:
- **Storage**: ~$0.018 per GB per month
- **Transactions**: ~$0.0004 per 10,000 operations
- **100 songs (~500 MB)**: less than $0.01 per month

Downloads by players count as outbound data transfer, which is billed separately once you pass
the free monthly allowance.

## Next steps

Once configured, you can:
1. Upload playlists from the app
2. Generate the Tabletop Simulator objects with real URLs
3. Load the music in Tabletop Simulator

Happy mixing! 🎵
