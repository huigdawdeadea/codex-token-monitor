# Token Monitor 项目协作规则

## 活跃预防规则

- `Pattern-Key: token-monitor.build-exclusive-output`：触发条件：测试、构建或发布会写入共享项目和配置的输出目录。 -> 必须动作：相关命令顺序执行；最终构建前只停止占用目标输出目录的隔离验证实例，不得误停用户正在使用的发布实例。 -> 验证标准：没有输出文件锁，目标测试与构建成功，发布实例状态符合本次授权范围。
- `Pattern-Key: token-monitor.acceptance-visible-path`：触发条件：准备声称启动、激活、托盘退出或重新启动问题已经修复。 -> 必须动作：使用与用户相同的 Shell 启动路径，分别验证进程、监听器和可见页面；端口或实例替换后使用新的浏览器页签。 -> 验证标准：页面真实可见且可访问，进程和监听器生命周期正确，托盘退出后可以全新启动。
- `Pattern-Key: token-monitor.cost-semantics`：触发条件：实现或展示 Token Monitor 的“额度预估”或成本估算。 -> 必须动作：只用已观测 token 计算近似金额，不外推未来 token；明确区分 API 等价参考金额与真实账单，并显示定价依据和不支持模型边界。 -> 验证标准：UI 与测试体现上述区分，金额来源能够追溯到已观测用量和适用价格。
- `Pattern-Key: token-monitor.sqlite-live-data-safety`：触发条件：已获授权修复或迁移实时 Monitor SQLite。 -> 必须动作：先停止持有数据库的进程并创建带时间戳备份，只保留类型完整的有效数据；备份不得自动删除。 -> 验证标准：完成后通过 SQLite 完整性检查以及关键只读查询或 API 验证，并报告保留的备份位置。
- `Pattern-Key: token-monitor.ui-contract-current-behavior`：触发条件：有意改变仪表盘行为、文案或数据来源。 -> 必须动作：同步更新契约测试以断言当前用户可见行为和稳定入口，移除对旧文案及已删除实现细节的要求，同时保留桌面和移动端边界。 -> 验证标准：相关 UI 契约测试和 JavaScript 语法检查通过。
