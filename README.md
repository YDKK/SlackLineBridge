# SlackLineBridge

<img src="https://user-images.githubusercontent.com/3415240/68022833-cf2dac80-fce8-11e9-96e8-c8a1c08a6352.png" width=128 />  
Slack &lt;--> LINE Bridge

Slackへの投稿とLINEへの投稿を相互に橋渡しします．

## 仕組み

![image](https://user-images.githubusercontent.com/3415240/68023213-eae58280-fce9-11e9-9fde-f7219f2bf66f.png)

## 設定

### Slack側の設定

1. 対象のチャンネルにIncoming WebhooksとOutgoing Webhooksを設定する．

1. Outgoing WebhooksのPOST先を `https://ホスト名/slack` に設定する．

### LINE側の設定

1. LINE DevelopersからMessaging APIチャンネルを作成する．

1. Webhook URLを `https://ホスト名/line` に設定する．

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
      "token": "", // Outgoing Webhooksのトークン
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
  "lineAccessToken": "" // LINEのアクセストークン（ロングターム）
}
```

## スクリーンショット

Slack側  
![image](https://user-images.githubusercontent.com/3415240/68024762-5f222500-fcee-11e9-83ed-7d6754804311.png)

LINE側  
![image](https://user-images.githubusercontent.com/3415240/68024767-63e6d900-fcee-11e9-9c78-c35b12d049ae.png)

## 制約

- LINEスタンプはLINE→Slackの一方向のみの対応です．
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
