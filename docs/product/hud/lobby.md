# 启动页与大厅

> **读者**：玩家
> **一句话**：进对局之前那一页长什么样，呼号与阵营怎么选，`DEPLOY` 之后发生什么。
> **数值出处**：`frontend/src/index.html`（页面与键位表）、`frontend/src/app/boot.js`
> （连接、大厅、部署）、`frontend/src/core/config.js` 的 `NAME_KEY`、
> `frontend/webpack.config.js`（版本号注入）
> **核对**：1.39.0 / 2026-11-20

## 1. 启动页是一个铺满窗口的页面

打开 `<host>:8010/`（靶场 `:8011/`）看到的就是它：深色径向渐变底，自上而下依次是

| 区块 | 内容 |
| --- | --- |
| 标题 | `MECH`，大号字、绿色发光 |
| 副标题 | `16 KM SQUARE FRONT · RED VS BLUE · THREE LANES` |
| 版本号 | 副标题下一行小字 `v<版本>` |
| 键位表 | 两列十行，左边是键、右边是一句话说明 |
| 大厅卡片 | `PILOT ID` 一行、`SIDE` 一行 |
| 主按钮 | `DEPLOY` |
| 状态行 | 连接进度：`CONNECTING...` → `LOADING 16 KM MAP...` → `READY` |
| 错误行 | 屏幕左下角红字，出现时才有 |

靶场服务器把副标题换成
`WEAPON RANGE · 4 KM MAP · APC TARGETS · REARM ANYWHERE`，状态行进
`LOADING RANGE...`，其余一模一样。

## 2. 版本号

* 位置：副标题下面那一行小字，形如 `v1.39.0`。
* 来源：`MechServer.csproj` 的 `<Version>`，**构建时**由 `frontend/webpack.config.js`
  读出来填进页面。
* 所以它是这一份 `assets/` 的版本：读到旧版本号说明客户端没重新构建，服务器版本
  与页面上的数字是两件事。

## 3. 呼号 `PILOT ID`

* 一个文本框，默认 `PILOT-<pid>`，最多 16 个字符。
* 呼号存在浏览器里（`mech.name`）：点 `DEPLOY` 时写进去，刷新页面后自动预填上一次
  用的那个，可以直接改。
* 对局里显示在消息栈（`DEPLOYED AS <呼号>`）、玩家名单与击杀行上；AI 机甲用名字
  池加 `[bot]` 后缀，见 [messages.md](messages.md)。
* 浏览器禁用了本地存储（无痕模式）时预填失效，大厅直接起在 `PILOT-<pid>` 上。

## 4. 阵营 `SIDE`

* 三个按钮：`RED` / `BLUE` / `AUTO`，选中的那个按阵营填色（红 / 蓝 / 绿）。
* 默认落在 `AUTO`：由服务器分配，去人少的一方；两边一样多时去红方。
* 阵营决定机甲、基地与告警盘上敌我的读法，部署之后不再改。
* 靶场同样由大厅选边，`AUTO` 照常平衡——两个窗口开两个浏览器，第二个就落在对面。

## 5. `DEPLOY`

1. 点 `DEPLOY`：客户端把呼号与阵营发出去，记住呼号，隐藏启动页，请求指针锁定，
   开始帧循环。
2. 服务器生成一台机甲、回一条 `joined`，消息栈读 `DEPLOYED AS <呼号>`。
3. 这个按钮**只算一次**：部署时它就交还了焦点，进对局后再按 `ENTER` 不会重新
   部署，也不会免费重生。重生走死亡倒计时或调试消息 `respawn`。
4. 连接失败时状态行读 `CONNECTION FAILED - IS THE SERVER RUNNING?`，`DEPLOY`
   没有反应，错误行给出异常原文（`BOOT ERROR:` / `ERROR:` / `REJECT:`）。

大厅里选不了武器：机甲一律以标准双臂（左 30 mm 机炮 + 右无制导火箭巢）出生，
换装是进对局后在基地的 `B` 面板里买卖，见 [panels.md](panels.md)。

## 6. 相关页

* 进对局的完整流程：[flow.md](../flow.md)
* 按键总表：[controls.md](../controls.md)
* 换装面板：[hud/panels.md](panels.md)
* 出生点规则：[rules/spawn.md](../rules/spawn.md)
