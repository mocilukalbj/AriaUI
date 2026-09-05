# 对应源码与构建参考声明 (Source References)

根据 GNU 通用公共许可证第 2 版（GPLv2）第 3 节及 MIT 许可证要求，本文件明确记录分发版本中所有对应源代码的版本、提交哈希、获取途径及复现构建说明。

---

## 1. AriaUI 桌面端应用程序源码

- **仓库位置**: 本地 Git 仓库 / GitHub 官方镜像
- **发布分支**: `main`
- **对应 Git 提交哈希 (Commit SHA)**: `9d3a4cfe35a16d9f53693c251323c0b610bdec3e`
- **主要原生桥接代码位置**:
  - `native/bridge/aria2_bridge.h`: 纯 C ABI 导出头文件与结构体定宽定义（ABI 版本: 1）
  - `native/bridge/aria2_bridge.cpp`: C ABI 桥接实现（RAII 句柄管理、无锁定宽事件环形队列、异常安全封锁）
  - `Services/Engine/NativeAriaEngineHost.cs`: C# 宿主与原生桥接 P/Invoke 绑定、事件轮询、命令批处理与故障隔离

---

## 2. 上游 aria2 (libaria2) 对应源码

AriaUI 二进制包内含的 `libaria2.so` 严格基于官方发布版本 `1.37.0` 构建。

- **上游官方仓库**: `https://github.com/aria2/aria2.git`
- **官方发布 Tag**: `release-1.37.0`
- **对应 Git 提交哈希 (Commit SHA)**: `02f2d0d8472b3c38c29b4dba8c75ebd5fdd2899a`
- **官方源码压缩包**: `https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0.tar.gz`
- **构建配置参数 (Locked Configure Flags)**:
  ```bash
  ./configure \
      --enable-shared \
      --disable-static \
      --enable-libaria2 \
      --disable-nls \
      --without-gnutls \
      --with-openssl \
      --without-libssh2 \
      --without-sqlite3 \
      --without-libuv
  ```
  上述配置确保 `libaria2.so` 仅依赖 OpenSSL、zlib 和 libxml2，排除不必要的外部依赖，达到最佳内存精简与稳定性。

---

## 3. 源码获取与完整复现指引 (How to Obtain and Rebuild)

任何用户或再分发者均可通过执行以下步骤完全自源码构建并复现发布的二进制产物：

```bash
# 1. 克隆 AriaUI 源码仓库
git clone <repository_url> AriaUI
cd AriaUI
git checkout 9d3a4cfe35a16d9f53693c251323c0b610bdec3e

# 2. 获取并解压 upstream aria2 1.37.0 源码
wget https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0.tar.gz
tar -xzf aria2-1.37.0.tar.gz
cd aria2-1.37.0
./configure --enable-shared --disable-static --enable-libaria2 --disable-nls --without-gnutls --with-openssl --without-libssh2 --without-sqlite3 --without-libuv
make -j$(nproc)
cd ..

# 3. 运行自动化发布打包脚本
chmod +x packaging/build_release.sh
./packaging/build_release.sh
```
构建脚本将自动验证、编译、打包并生成匹配的 SHA256 校验和。
