# BTCPay Server Chia Plugin

This repository contains the source code for the BTCPay Server plugin that enables the receipt of XCH payments on the Chia blockchain.
The plugin extends the functionality of BTCPay Server, a self-hosted cryptocurrency payment processor that allows merchants to accept Bitcoin and other cryptocurrencies.

## 🎨 Features

- **XCH Payments**: Receive XCH payments directly on your BTCPay Server instance.
- **Invoice Generation**: Generate invoices with XCH addresses as payment reception.
- **Blockchain Monitoring**: Scan the Chia blockchain to detect payments in full, overpaid, or partial amounts.
- **Automatic Settlement**: Continuously verify the Chia blockchain to settle payments securely and efficiently.

## Supported tokens on Chia

- [x] XCH

## 📗 Requirements

- BTCPay Server: Make sure you have a running instance of BTCPay Server. You can find more information and installation instructions [here](https://docs.btcpayserver.org/).
- Chia Wallet: Set up a Chia wallet (e.g. chia.net, Sage... ) to generate a wallet and get a master public key.

## 🚀 Installation

Install the plugin from the BTCPay Server > Settings > Plugin > Available Plugins, and restart.

## 💚 Support

For any questions, issues, or feedback related to the BTCPay Server Chia Plugin, please [open an issue](https://github.com/XCHPay/btcpayserver-chia-plugin/issues) in this repository.

## 📝 License

This project is licensed under the [MIT License](LICENSE).
