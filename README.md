# SlackLineBridge

<img src="https://img.shields.io/github/license/YDKK/SlackLineBridge" /> <a href="https://hub.docker.com/r/ydkk/slack-line-bridge"><img src="https://img.shields.io/docker/v/ydkk/slack-line-bridge/latest" /></a>  

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

## スクリーンショット

Slack側  
![image](https://user-images.githubusercontent.com/3415240/128625338-13d2384e-3207-4ab6-92d8-faa7cf6539cd.png)


LINE側  
![image](https://user-images.githubusercontent.com/3415240/128625349-cd4c8dcc-bb36-4193-b3af-4cac9ef69853.png)

## 制約

- ~~SlackのユーザアイコンはLINE側には反映されません．~~
    - v4.0.0でLINE側のユーザアイコンとユーザ名の置き換えに対応しました ([#24](https://github.com/YDKK/SlackLineBridge/pull/24))
    - 上のスクリーンショットは更新前のものです

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
