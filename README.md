# SlackLineBridge

<img src="https://img.shields.io/github/license/YDKK/SlackLineBridge" /> <a href="https://hub.docker.com/r/ydkk/slack-line-bridge"><img src="https://img.shields.io/docker/cloud/build/ydkk/slack-line-bridge" /></a>  

<img src="https://user-images.githubusercontent.com/3415240/68022833-cf2dac80-fce8-11e9-96e8-c8a1c08a6352.png" width=128 />  
Slack &lt;--> LINE Bridge

Slackへの投稿とLINEへの投稿を相互に橋渡しします．

## 仕組み

![image](https://user-images.githubusercontent.com/3415240/128625515-ea5eb6b7-8680-4ddc-97de-ce4ec896b739.png)


## 設定

### Slack側の設定

1. Slack Appを作成する
    - `Event Subscriptions` を有効にし、Request URLを `https://ホスト名/slack2` に設定する
    - `Subscribe to bot events` で `message.channels` と `message.groups` イベントを購読する
2. 作成したSlack Appをワークスペースにインストールする
3. 対象のチャンネルにインストールしたSlack Appを参加させる
4. 対象のチャンネルにIncoming Webhooksを設定する

### LINE側の設定

1. LINE DevelopersからMessaging APIチャンネルを作成する．
2. Webhook URLを `https://ホスト名/line` に設定する．

### appsettings.json

`UseCloudWatchLogs` : ログデータをAmazon CloudWatch Logsに書き出すかどうか

### appsettings.AWS.json (Optional)

Amazon CloudWatch Logsを使う場合，AWSの資格情報を指定することが可能です．  
指定しなかった場合は[この辺](https://docs.aws.amazon.com/ja_jp/cli/latest/userguide/cli-configure-files.html)から適当に探してくるはず．

```json
{
  "AWS": {
    "AccessKey": "",
    "SecretKey": ""
  }
}

```

### config.json

関連付けるチャンネルの設定

```jsonc
{
  "slackChannels": [ // Slackチャンネルリストの定義
    {
      "name": "hoge",
      "teamId": "",
      "channelId": "",
      "webhookUrl": "" // Incoming WebhooksのUrl
    }
  ],
  "lineChannels": [ // LINEチャンネル（user/group/room）リストの定義
    {
      "name": "fuga",
      "id": "" // 対象のID
    }
  ],
  "slackLineBridges": [ // 関連付けるチャンネルリストの定義
    {
      "slack": "hoge",
      "line": "fuga"
    }
  ],
  "lineAccessToken": "", // LINEのアクセストークン（ロングターム）
  "lineChannelSecret": "", // LINEのチャンネルシークレット
  "slackSigningSecret": "", // Slack AppのSigning Secret
  "slackAccessToken": "" // Slack AppのBot User OAuth Token
}
```

### S3互換メディアキャッシュ（任意）

LINE/Slackの署名を検証した後、取得済みメディアをprivateなS3互換バケットに保存します。`config.json` の `MediaCache` で有効化します。未設定または `Enabled: false` では従来どおり上流から配信します。

```jsonc
{
  "MediaCache": {
    "Enabled": true,
    "MaxObjectBytes": 268435456, // 最大256 MiB
    "Endpoint": "https://s3.example.com", // AWS S3では空欄
    "Region": "us-east-1",
    "Bucket": "private-media-cache",
    "KeyPrefix": "slack-line-bridge/media",
    "ForcePathStyle": true, // MinIOなどで必要な場合
    "AccessKey": "", // 両方空欄ならAWS SDK標準のcredential chainを使用
    "SecretKey": ""
  }
}
```

有効時は `Region` と `Bucket` が必須です。明示資格情報を使う場合はAccessKey/SecretKeyを両方設定します。資格情報には対象prefix内の `s3:GetObject` と `s3:PutObject`、およびバケットの `s3:ListBucket` を許可してください。AWSではListBucket権限がないと不存在キーが403になる場合があり、このキャッシュは403をミスとして扱いません。バケットは公開せず、AWS側のBlock Public Accessも有効にしてください。保存キーにはprovider名とメディアID/URLのSHA-256だけを使います。

キャッシュミス時に一時保存するオブジェクトの上限は `MaxObjectBytes`（既定256 MiB）です。超過した取得は失敗し、一時ファイルを削除します。

画像・動画・その他のファイルをMIME typeに関係なく保存します。オブジェクトの自動削除は行わないため、必要ならバケットのLifecycleルールで保持期間を設定してください。ミス時は上流の全データを一時ファイルへ保存してからS3へ登録するため、OSの一時領域には同時取得中のファイル合計を置ける空き容量が必要です。メモリには固定サイズのbufferで転送します。

S3の読み書きに失敗した場合はキャッシュミスとして扱わずHTTP 503を返します。上流HTTPエラーのstatus codeは保持し、エラー本文はキャッシュしません。

## スクリーンショット

Slack側  
![image](https://user-images.githubusercontent.com/3415240/128625338-13d2384e-3207-4ab6-92d8-faa7cf6539cd.png)


LINE側  
![image](https://user-images.githubusercontent.com/3415240/128625349-cd4c8dcc-bb36-4193-b3af-4cac9ef69853.png)

## 制約

- SlackのユーザアイコンはLINE側には反映されません．

## Dockerイメージ

https://hub.docker.com/r/ydkk/slack-line-bridge

`docker-compose.yml` の例
```yml
version: '3'
services:
  bridge:
    restart: always
    image: ydkk/slack-line-bridge:latest
    volumes:
      - ./appsettings.AWS.json:/app/appsettings.AWS.json:ro
      - ./appsettings.json:/app/appsettings.json:ro
      - ./config.json:/app/config.json:ro
    networks:
      - bridge_external_network
networks:
  bridge_external_network:
    external: true
```

## License

MIT
